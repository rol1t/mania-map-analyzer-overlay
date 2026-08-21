using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Discovers analyzer-engine manifests without activating their JavaScript
/// runtime. The eventual host can use a valid package's script paths to build
/// its own process/WebView worker adapter.
/// </summary>
public sealed class AnalyzerEngineCatalog
{
    public const int SupportedManifestSchemaVersion = 1;
    public const string ExpectedKind = "analyzer-engine";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly Lazy<AnalyzerEngineCatalogSnapshot> _snapshot;
    private readonly IAnalyzerEngineDiagnosticSink _diagnosticSink;

    public AnalyzerEngineCatalog()
        : this(Path.Combine(AppPaths.BaseDirectory, "Assets", "analyzer-engines"), null)
    {
    }

    public AnalyzerEngineCatalog(string rootDirectory)
        : this(rootDirectory, null)
    {
    }

    public AnalyzerEngineCatalog(
        string rootDirectory,
        IAnalyzerEngineDiagnosticSink? diagnosticSink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _diagnosticSink = diagnosticSink ?? new AppLoggerDiagnosticSink();
        _snapshot = new Lazy<AnalyzerEngineCatalogSnapshot>(LoadPackages);
    }

    public string RootDirectory
    {
        get;
    }

    public IReadOnlyList<AnalyzerEnginePackage> List() => _snapshot.Value.Packages;

    public IReadOnlyList<AnalyzerEnginePackage> Available() =>
        List().Where(package => package.IsAvailable).ToArray();

    public IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics => _snapshot.Value.Diagnostics;

    public AnalyzerEnginePackage? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return List().FirstOrDefault(package =>
            string.Equals(package.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a valid package by id. No runtime, worker, or WebView is started.
    /// </summary>
    public AnalyzerEnginePackage Require(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalized = id.Trim();
        var package = List().FirstOrDefault(candidate =>
            candidate.IsAvailable &&
            string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (package is not null)
        {
            return package;
        }

        var unavailable = Get(normalized);
        var detail = unavailable is null
            ? "was not found"
            : "is unavailable: " + string.Join("; ", unavailable.Diagnostics.Select(diagnostic => diagnostic.Code));
        throw new FileNotFoundException(
            $"Analyzer engine '{normalized}' {detail}. Rebuild the package so Assets/analyzer-engines is included.",
            RootDirectory);
    }

    private AnalyzerEngineCatalogSnapshot LoadPackages()
    {
        var packages = new List<AnalyzerEnginePackage>();
        var diagnostics = new List<AnalyzerEngineDiagnostic>();

        if (!Directory.Exists(RootDirectory))
        {
            var diagnostic = CreateDiagnostic(
                "engine.root_missing",
                $"Analyzer engine package directory '{RootDirectory}' was not found.",
                RootDirectory,
                new DirectoryNotFoundException(RootDirectory));
            diagnostics.Add(diagnostic);
            LogDiagnostic("Discovering analyzer engine packages", diagnostic);
            return new AnalyzerEngineCatalogSnapshot(packages, diagnostics);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(RootDirectory)
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            var diagnostic = CreateDiagnostic(
                "engine.discovery_failed",
                $"Analyzer engine package directory '{RootDirectory}' could not be enumerated.",
                RootDirectory,
                exception);
            diagnostics.Add(diagnostic);
            LogDiagnostic("Discovering analyzer engine packages", diagnostic, exception);
            return new AnalyzerEngineCatalogSnapshot(packages, diagnostics);
        }

        if (directories.Length == 0)
        {
            var diagnostic = CreateDiagnostic(
                "engine.no_packages",
                $"Analyzer engine package directory '{RootDirectory}' does not contain any package directories.",
                RootDirectory,
                new InvalidDataException("No analyzer engine packages were discovered."));
            diagnostics.Add(diagnostic);
            LogDiagnostic("Discovering analyzer engine packages", diagnostic);
            return new AnalyzerEngineCatalogSnapshot(packages, diagnostics);
        }

        string physicalCatalogRoot;
        try
        {
            physicalCatalogRoot = ResolvePhysicalPath(RootDirectory);
        }
        catch (Exception exception)
        {
            var diagnostic = CreateDiagnostic(
                "engine.root_physical_resolution_failed",
                $"Analyzer engine package directory '{RootDirectory}' could not be resolved physically.",
                RootDirectory,
                exception);
            diagnostics.Add(diagnostic);
            LogDiagnostic("Discovering analyzer engine packages", diagnostic, exception);
            return new AnalyzerEngineCatalogSnapshot(packages, diagnostics);
        }

        foreach (var directory in directories)
        {
            AnalyzerEnginePackage package;
            try
            {
                var physicalPackageDirectory = ResolvePhysicalPath(directory);
                if (!IsPathContained(physicalCatalogRoot, physicalPackageDirectory))
                {
                    var diagnostic = CreateDiagnostic(
                        "engine.package_directory_escape",
                        $"Analyzer engine package directory '{directory}' resolves outside '{RootDirectory}'.",
                        directory,
                        new InvalidDataException(
                            "The analyzer engine package directory resolves outside its catalog directory."));
                    LogDiagnostic($"Loading analyzer engine package '{directory}'", diagnostic);
                    package = CreateUnavailablePackage(directory, diagnostic);
                }
                else
                {
                    package = LoadPackage(directory);
                }
            }
            catch (Exception exception)
            {
                var diagnostic = CreateDiagnostic(
                    "engine.package_load_failed",
                    $"Analyzer engine package '{directory}' could not be loaded.",
                    directory,
                    exception);
                LogDiagnostic($"Loading analyzer engine package '{directory}'", diagnostic, exception);
                package = CreateUnavailablePackage(directory, diagnostic);
            }

            packages.Add(package);
            diagnostics.AddRange(package.Diagnostics);
        }

        foreach (var group in packages
                     .Where(package => !string.IsNullOrWhiteSpace(package.Id))
                     .GroupBy(package => package.Id!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var message = $"Analyzer engine id '{group.Key}' is declared by more than one package.";
            foreach (var package in group)
            {
                var diagnostic = CreateDiagnostic("engine.duplicate_id", message, package.ManifestPath);
                LogDiagnostic($"Loading analyzer engine package '{package.PackageDirectory}'", diagnostic);
                var replacement = package.AddDiagnostic(diagnostic);
                packages[packages.IndexOf(package)] = replacement;
                diagnostics.Add(diagnostic);
            }
        }

        return new AnalyzerEngineCatalogSnapshot(
            packages.OrderBy(package => package.Name ?? package.PackageDirectory, StringComparer.OrdinalIgnoreCase),
            diagnostics);
    }

    private static AnalyzerEnginePackage CreateUnavailablePackage(
        string directory,
        AnalyzerEngineDiagnostic diagnostic)
    {
        var packageDirectory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        return new AnalyzerEnginePackage(
            packageDirectory,
            manifestPath,
            null,
            null,
            null,
            new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Unavailable, [diagnostic]));
    }

    private AnalyzerEnginePackage LoadPackage(string directory)
    {
        var packageDirectory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            var diagnostic = CreateDiagnostic(
                "engine.manifest_missing",
                $"Analyzer engine package '{packageDirectory}' does not contain manifest.json.",
                manifestPath,
                new FileNotFoundException("Analyzer engine manifest was not found.", manifestPath));
            LogDiagnostic($"Loading analyzer engine package '{packageDirectory}'", diagnostic);
            return new AnalyzerEnginePackage(
                packageDirectory,
                manifestPath,
                null,
                null,
                null,
                new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Missing, [diagnostic]));
        }

        AnalyzerEngineManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AnalyzerEngineManifest>(
                File.ReadAllText(manifestPath), _jsonOptions);
        }
        catch (Exception exception)
        {
            var diagnostic = CreateDiagnostic(
                "engine.manifest_parse_failed",
                $"Analyzer engine manifest '{manifestPath}' could not be parsed.",
                manifestPath,
                exception);
            LogDiagnostic($"Loading analyzer engine manifest '{manifestPath}'", diagnostic, exception);
            return new AnalyzerEnginePackage(
                packageDirectory,
                manifestPath,
                null,
                null,
                null,
                new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Invalid, [diagnostic]));
        }

        var validationDiagnostics = ValidateManifest(manifest, manifestPath);
        if (validationDiagnostics.Count > 0)
        {
            foreach (var diagnostic in validationDiagnostics)
            {
                LogDiagnostic($"Loading analyzer engine manifest '{manifestPath}'", diagnostic);
            }

            return new AnalyzerEnginePackage(
                packageDirectory,
                manifestPath,
                manifest,
                null,
                null,
                new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Invalid, validationDiagnostics));
        }

        string? runtimePath = null;
        string? workerPath = null;
        var resourceDiagnostics = new List<AnalyzerEngineDiagnostic>();
        try
        {
            runtimePath = ResolveContainedFile(packageDirectory, manifest!.Runtime, requireExistingFile: true);
        }
        catch (Exception exception)
        {
            resourceDiagnostics.Add(CreateDiagnostic(
                "engine.runtime_unavailable",
                $"Analyzer engine runtime '{manifest!.Runtime}' is not a file inside package '{packageDirectory}'.",
                manifestPath,
                exception));
        }

        try
        {
            workerPath = ResolveContainedFile(packageDirectory, manifest!.Worker, requireExistingFile: true);
        }
        catch (Exception exception)
        {
            resourceDiagnostics.Add(CreateDiagnostic(
                "engine.worker_unavailable",
                $"Analyzer engine worker '{manifest!.Worker}' is not a file inside package '{packageDirectory}'.",
                manifestPath,
                exception));
        }

        if (resourceDiagnostics.Count > 0)
        {
            foreach (var diagnostic in resourceDiagnostics)
            {
                LogDiagnostic($"Loading analyzer engine package '{packageDirectory}'", diagnostic);
            }

            return new AnalyzerEnginePackage(
                packageDirectory,
                manifestPath,
                manifest,
                runtimePath,
                workerPath,
                new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Missing, resourceDiagnostics));
        }

        return new AnalyzerEnginePackage(
            packageDirectory,
            manifestPath,
            manifest,
            runtimePath,
            workerPath,
            new AnalyzerEngineAvailability(AnalyzerEngineAvailabilityStatus.Available));
    }

    private static List<AnalyzerEngineDiagnostic> ValidateManifest(
        AnalyzerEngineManifest? manifest,
        string manifestPath)
    {
        var diagnostics = new List<AnalyzerEngineDiagnostic>();
        if (manifest is null)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.manifest_empty",
                $"Analyzer engine manifest '{manifestPath}' is empty.",
                manifestPath));
            return diagnostics;
        }

        if (manifest.ManifestSchemaVersion != SupportedManifestSchemaVersion)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.schema_unsupported",
                $"Manifest schema version {manifest.ManifestSchemaVersion} is not supported; expected {SupportedManifestSchemaVersion}.",
                manifestPath));
        }

        if (!string.Equals(manifest.Kind?.Trim(), ExpectedKind, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.kind_invalid",
                $"Manifest '{manifestPath}' must declare kind '{ExpectedKind}'.",
                manifestPath));
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.id_missing",
                $"Manifest '{manifestPath}' is missing a non-empty id.",
                manifestPath));
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.version_missing",
                $"Manifest '{manifestPath}' is missing a non-empty version.",
                manifestPath));
        }

        if (string.IsNullOrWhiteSpace(manifest.Protocol))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.protocol_missing",
                $"Manifest '{manifestPath}' is missing a non-empty protocol.",
                manifestPath));
        }

        if (manifest.ProtocolVersion <= 0)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.protocol_version_invalid",
                $"Manifest '{manifestPath}' must declare a positive protocolVersion.",
                manifestPath));
        }

        ValidateManifestResource(manifest.Runtime, "runtime", manifestPath, diagnostics);
        ValidateManifestResource(manifest.Worker, "worker", manifestPath, diagnostics);
        ValidateUpstream(manifest.Upstream, manifestPath, diagnostics);
        ValidateCapabilities(manifest.Capabilities, manifestPath, diagnostics);
        return diagnostics;
    }

    private static void ValidateManifestResource(
        string? relativePath,
        string fieldName,
        string manifestPath,
        List<AnalyzerEngineDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine." + fieldName + "_missing",
                $"Manifest '{manifestPath}' is missing a non-empty {fieldName} path.",
                manifestPath));
            return;
        }

        try
        {
            EnsureRelativePath(relativePath);
        }
        catch (Exception exception)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine." + fieldName + "_path_invalid",
                $"Manifest '{manifestPath}' contains a {fieldName} path outside its package directory.",
                manifestPath,
                exception));
        }
    }

    private static void ValidateUpstream(
        AnalyzerEngineUpstreamManifest? upstream,
        string manifestPath,
        List<AnalyzerEngineDiagnostic> diagnostics)
    {
        if (upstream is null)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.upstream_missing",
                $"Manifest '{manifestPath}' is missing the upstream object.",
                manifestPath));
            return;
        }

        ValidateRequiredValue(upstream.Name, "upstream.name", manifestPath, diagnostics);
        ValidateRequiredValue(upstream.Repository, "upstream.repository", manifestPath, diagnostics);
        ValidateRequiredValue(upstream.License, "upstream.license", manifestPath, diagnostics);
        ValidateRequiredValue(upstream.Integration, "upstream.integration", manifestPath, diagnostics);

        var supportedVersions = new List<string>();
        if (!string.IsNullOrWhiteSpace(upstream.Version))
        {
            supportedVersions.Add(upstream.Version.Trim());
        }

        if (upstream.SupportedVersions is not null)
        {
            supportedVersions.AddRange(upstream.SupportedVersions
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version.Trim()));
        }

        if (supportedVersions.Count == 0)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.upstream_version_missing",
                $"Manifest '{manifestPath}' must declare at least one supported upstream version.",
                manifestPath));
        }

        if (upstream.SupportedVersions is not null &&
            upstream.SupportedVersions.Any(string.IsNullOrWhiteSpace))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.upstream_version_invalid",
                $"Manifest '{manifestPath}' contains an empty supported upstream version.",
                manifestPath));
        }
    }

    private static void ValidateCapabilities(
        AnalyzerEngineCapabilitiesManifest? capabilities,
        string manifestPath,
        List<AnalyzerEngineDiagnostic> diagnostics)
    {
        if (capabilities is null)
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.capabilities_missing",
                $"Manifest '{manifestPath}' is missing the capabilities object.",
                manifestPath));
            return;
        }

        ValidateStringList(capabilities.Algorithms, "capabilities.algorithms", manifestPath, diagnostics, requireValues: true);
        ValidateStringList(
            capabilities.SemanticMetricIds,
            "capabilities.semanticMetricIds",
            manifestPath,
            diagnostics,
            requireValues: true);

        if (capabilities.OptionalAlgorithms is null)
        {
            return;
        }

        foreach (var optional in capabilities.OptionalAlgorithms)
        {
            if (string.IsNullOrWhiteSpace(optional.Key))
            {
                diagnostics.Add(CreateDiagnostic(
                    "engine.optional_algorithm_id_missing",
                    $"Manifest '{manifestPath}' contains an optional algorithm without an id.",
                    manifestPath));
                continue;
            }

            if (optional.Value is null)
            {
                diagnostics.Add(CreateDiagnostic(
                    "engine.optional_algorithm_invalid",
                    $"Optional algorithm '{optional.Key}' in manifest '{manifestPath}' has no capability object.",
                    manifestPath));
                continue;
            }

            if (optional.Value.RequiresRuntimeProbe)
            {
                ValidateStringList(
                    optional.Value.RequiresExports,
                    "capabilities.optionalAlgorithms." + optional.Key + ".requiresExports",
                    manifestPath,
                    diagnostics,
                    requireValues: true);
            }
            else if (optional.Value.RequiresExports is not null)
            {
                ValidateStringList(
                    optional.Value.RequiresExports,
                    "capabilities.optionalAlgorithms." + optional.Key + ".requiresExports",
                    manifestPath,
                    diagnostics,
                    requireValues: false);
            }
        }
    }

    private static void ValidateStringList(
        IReadOnlyList<string>? values,
        string fieldName,
        string manifestPath,
        List<AnalyzerEngineDiagnostic> diagnostics,
        bool requireValues)
    {
        if (values is null || (requireValues && values.Count == 0))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.capability_list_missing",
                $"Manifest '{manifestPath}' is missing non-empty {fieldName}.",
                manifestPath));
            return;
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.capability_list_invalid",
                $"Manifest '{manifestPath}' contains an empty value in {fieldName}.",
                manifestPath));
        }

        if (values.Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.capability_list_duplicate",
                $"Manifest '{manifestPath}' contains duplicate values in {fieldName}.",
                manifestPath));
        }
    }

    private static void ValidateRequiredValue(
        string? value,
        string fieldName,
        string manifestPath,
        List<AnalyzerEngineDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(CreateDiagnostic(
                "engine.upstream_field_missing",
                $"Manifest '{manifestPath}' is missing non-empty {fieldName}.",
                manifestPath));
        }
    }

    internal static string ResolveContainedFile(
        string directory,
        string relativePath,
        bool requireExistingFile)
    {
        EnsureRelativePath(relativePath);
        var root = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathContained(root, fullPath))
        {
            throw new InvalidDataException("The analyzer engine resource path escapes its package directory.");
        }

        if (requireExistingFile && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The analyzer engine resource was not found.", fullPath);
        }

        if (requireExistingFile)
        {
            var physicalRoot = ResolvePhysicalPath(root);
            var physicalFile = ResolvePhysicalPath(fullPath);
            if (!IsPathContained(physicalRoot, physicalFile))
            {
                throw new InvalidDataException(
                    "The analyzer engine resource resolves outside its physical package directory.");
            }
        }

        return fullPath;
    }

    private static bool IsPathContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            normalizedRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, candidate, comparison) ||
               candidate.StartsWith(rootPrefix, comparison);
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw new InvalidDataException("The analyzer engine path has no filesystem root.");
        }

        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, segment));
            if (Directory.Exists(candidate))
            {
                current = ResolveLinkTarget(candidate, isDirectory: true);
            }
            else if (File.Exists(candidate))
            {
                current = ResolveLinkTarget(candidate, isDirectory: false);
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string ResolveLinkTarget(string path, bool isDirectory)
    {
        FileSystemInfo fileSystemInfo = isDirectory
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        try
        {
            var linkTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
            return linkTarget is null ? Path.GetFullPath(path) : Path.GetFullPath(linkTarget.FullName);
        }
        catch (UnauthorizedAccessException)
        {
            // A protected parent directory may be visible to path APIs while
            // link metadata is not readable. Preserve the lexical path and
            // continue checking package-owned segments.
            return Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static void EnsureRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('\0'))
        {
            throw new InvalidDataException("Analyzer engine resource paths must be relative package paths.");
        }

        if (relativePath.Split(['/', '\\'], StringSplitOptions.None)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Analyzer engine resource paths must not contain parent-directory segments.");
        }
    }

    private static AnalyzerEngineDiagnostic CreateDiagnostic(
        string code,
        string message,
        string? path = null,
        Exception? exception = null) =>
        new(code, message, path, AnalyzerEngineDiagnosticSeverity.Error, exception);

    private void LogDiagnostic(
        string operation,
        AnalyzerEngineDiagnostic diagnostic,
        Exception? exception = null)
    {
        _diagnosticSink.Report(operation, diagnostic, exception ?? diagnostic.Exception);
    }

    private sealed class AppLoggerDiagnosticSink : IAnalyzerEngineDiagnosticSink
    {
        public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null)
        {
            AppLogger.Error(operation, exception ?? new InvalidDataException(diagnostic.Message));
        }
    }

    private sealed class AnalyzerEngineCatalogSnapshot
    {
        public AnalyzerEngineCatalogSnapshot(
            IEnumerable<AnalyzerEnginePackage> packages,
            IEnumerable<AnalyzerEngineDiagnostic> diagnostics)
        {
            Packages = packages.ToArray();
            Diagnostics = diagnostics.ToArray();
        }

        public IReadOnlyList<AnalyzerEnginePackage> Packages
        {
            get;
        }

        public IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics
        {
            get;
        }
    }
}
