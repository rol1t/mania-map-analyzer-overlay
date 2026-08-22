using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Loads trusted analyzer packages shipped with the application. A package may
/// contain editable bridge resources, but only explicitly registered adapter
/// implementations are instantiated.
/// </summary>
public sealed class AnalyzerAdapterCatalog
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly Lazy<IReadOnlyList<AnalyzerAdapterPackage>> _packages;

    public AnalyzerAdapterCatalog() => _packages = new Lazy<IReadOnlyList<AnalyzerAdapterPackage>>(LoadPackages);

    public string RootDirectory => Path.Combine(AppPaths.BaseDirectory, "Assets", "analyzers");
    public IReadOnlyList<AnalyzerAdapterPackage> List() => _packages.Value;

    public AnalyzerAdapterPackage Require(string? id)
    {
        var normalized = string.IsNullOrWhiteSpace(id) ? "mania-map-analyser" : id.Trim();
        return List().FirstOrDefault(x =>
                   string.Equals(x.Descriptor.Id, normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new FileNotFoundException(
                   $"Analyzer adapter '{normalized}' was not found. Rebuild the package so Assets/analyzers is included.",
                   RootDirectory);
    }

    private IReadOnlyList<AnalyzerAdapterPackage> LoadPackages()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var result = new List<AnalyzerAdapterPackage>();

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            AnalyzerAdapterManifest? manifest;

            try
            {
                manifest = JsonSerializer.Deserialize<AnalyzerAdapterManifest>(
                    File.ReadAllText(manifestPath), _jsonOptions);
            }
            catch (Exception exception)
            {
                AppLogger.Error($"Loading analyzer manifest '{manifestPath}'", exception);
                continue;
            }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) ||
                string.IsNullOrWhiteSpace(manifest.AnalysisPath) || string.IsNullOrWhiteSpace(manifest.Script))
            {
                AppLogger.Error(
                    "Loading analyzer manifest",
                    new InvalidDataException($"Manifest '{manifestPath}' is missing required fields."));
                continue;
            }

            var scriptPath = ResolveContainedFile(directory, manifest.Script);
            var descriptor = new AnalyzerDescriptor(
                manifest.Id.Trim(),
                string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id.Trim() : manifest.Name.Trim(),
                NormalizeRoute(manifest.AnalysisPath),
                NormalizeRoute(string.IsNullOrWhiteSpace(manifest.FullscreenPath)
                    ? manifest.AnalysisPath
                    : manifest.FullscreenPath),
                string.IsNullOrWhiteSpace(manifest.SettingsPath) ? null : NormalizeRoute(manifest.SettingsPath),
                manifest.SupportsFullscreen,
                manifest.SnapshotSchemaVersion);

            IAnalyzerAdapter adapter = new JsonAnalyzerAdapter(descriptor);
            result.Add(new AnalyzerAdapterPackage(
                adapter,
                directory,
                scriptPath,
                string.IsNullOrWhiteSpace(manifest.HostSelector) ? "body" : manifest.HostSelector.Trim(),
                string.IsNullOrWhiteSpace(manifest.PresetAnchorSelector)
                    ? null
                    : manifest.PresetAnchorSelector.Trim()));
        }

        return result;
    }

    private static string ResolveContainedFile(string directory, string relativePath)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("Analyzer adapter bridge script was not found.", fullPath);
        }

        return fullPath;
    }

    private static string NormalizeRoute(string route) => "/" + route.Trim().TrimStart('/');
}
