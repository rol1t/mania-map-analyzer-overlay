using System.Reflection;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Materializes immutable files embedded in the single-file launcher. The
/// target is content-addressed by the assembly MVID and published atomically.
/// </summary>
public static class RuntimeAssetDeployment
{
    internal const string AssetResourcePrefix = "ManiaMapAnalyzerOverlay.RuntimeAssets/";
    internal const string UpdaterResourceName = "ManiaMapAnalyzerOverlay.Tools/Updater";
    private const string ReadyMarker = ".ready";

    public static void Initialize()
    {
        var assembly = typeof(RuntimeAssetDeployment).Assembly;
        // Assembly.Location is empty for a single-file host. Only normal
        // developer/folder builds may trust loose resources beside the binary.
        if (!string.IsNullOrWhiteSpace(assembly.Location) &&
            Directory.Exists(Path.Combine(AppPaths.BaseDirectory, "Assets")))
        {
            AppPaths.UseResourceDirectory(AppPaths.BaseDirectory);
            return;
        }

        AppPaths.UseResourceDirectory(DeployEmbeddedRuntimeAssets(
            assembly,
            AppPaths.RuntimeDirectory));
    }

    public static string DeployEmbeddedRuntimeAssets(Assembly assembly, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(AssetResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
        {
            throw new InvalidOperationException("The launcher does not contain an embedded runtime asset payload.");
        }

        var root = Path.GetFullPath(runtimeRoot);
        Directory.CreateDirectory(root);
        var version = SanitizePathSegment(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0");
        var buildIdentity = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var target = Path.Combine(root, version + "-" + buildIdentity);
        if (File.Exists(Path.Combine(target, ReadyMarker)))
        {
            return target;
        }

        var temporary = Path.Combine(root, ".deploy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            foreach (var resourceName in resources)
            {
                var relative = resourceName[AssetResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var destination = ResolveContainedPath(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Embedded runtime resource '{resourceName}' could not be opened.");
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            ValidatePayload(temporary);
            File.WriteAllText(Path.Combine(temporary, ReadyMarker), buildIdentity);
            try
            {
                Directory.Move(temporary, target);
            }
            catch (IOException) when (File.Exists(Path.Combine(target, ReadyMarker)))
            {
                // Another process completed the identical immutable deployment.
            }
        }
        finally
        {
            TryDeleteDirectory(temporary);
        }

        if (!File.Exists(Path.Combine(target, ReadyMarker)))
        {
            throw new InvalidOperationException("The embedded runtime payload could not be published atomically.");
        }

        RemoveObsoleteDeployments(root, target);
        return target;
    }

    public static string? EnsureUpdaterExecutable()
    {
        var assembly = typeof(RuntimeAssetDeployment).Assembly;
        var embedded = DeployEmbeddedUpdater(assembly, AppPaths.ToolsDirectory);
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            return embedded;
        }

        var looseUpdater = Path.Combine(
            AppPaths.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "Mania Map Analyzer Overlay.Updater.exe"
                : "Mania Map Analyzer Overlay.Updater");
        return File.Exists(looseUpdater) ? looseUpdater : null;
    }

    public static string? DeployEmbeddedUpdater(Assembly assembly, string toolsRoot)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        using var resource = assembly.GetManifestResourceStream(UpdaterResourceName);
        if (resource is null || resource.Length == 0)
        {
            return null;
        }

        var buildIdentity = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var root = Path.GetFullPath(toolsRoot);
        var directory = Path.Combine(root, buildIdentity);
        var executable = Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? "Mania Map Analyzer Overlay.Updater.exe"
                : "Mania Map Analyzer Overlay.Updater");
        if (!File.Exists(executable) || new FileInfo(executable).Length != resource.Length)
        {
            Directory.CreateDirectory(directory);
            var temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    resource.CopyTo(output);
                }

                File.Move(temporary, executable, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        executable,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException) { }
            }
        }

        RemoveObsoleteDeployments(root, directory);
        return executable;
    }

    public static void ValidatePayload(string root)
    {
        string[] required =
        [
            "Assets/overlay/presets/companella/manifest.json",
            "Assets/overlay/runtime/renderer.js",
            "Assets/analyzers/mania-map-analyser/manifest.json",
            "Assets/analyzer-engines/mania-map-analyser/manifest.json",
            "Assets/analyzer-engines/mania-map-analyser/worker.mjs",
            "Assets/localization/manifest.json",
            "Assets/localization/en.json",
            "Assets/localization/ru.json",
        ];
        foreach (var relative in required)
        {
            if (!File.Exists(ResolveContainedPath(root, relative)))
            {
                throw new InvalidDataException($"Embedded runtime payload is missing '{relative}'.");
            }
        }
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Embedded runtime path must be relative: '{relative}'.");
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Embedded runtime path escapes its deployment root: '{relative}'.");
        }

        return fullPath;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static void RemoveObsoleteDeployments(string root, string current)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(directory, current, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).StartsWith(".deploy-", StringComparison.Ordinal) ||
                Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-7))
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
