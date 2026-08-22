using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Resolves shipped and user-editable overlay resources without embedding their
/// HTML, CSS, or JavaScript in the application binary.
/// </summary>
public sealed class OverlayPresetCatalog
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string BuiltInDirectory => Path.Combine(AppPaths.BaseDirectory, "Assets", "overlay", "presets");
    public string UserDirectory => Path.Combine(AppPaths.DataDirectory, "presets");

    public IReadOnlyList<OverlayPresetDefinition> List()
    {
        var result = new List<OverlayPresetDefinition>();
        AddDefinitions(result, BuiltInDirectory);
        AddDefinitions(result, UserDirectory);
        return result
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public OverlayPresetDefinition Get(string? id)
    {
        var normalized = string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
        return List().FirstOrDefault(x => string.Equals(x.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? new OverlayPresetDefinition();
    }

    public OverlayPresetDefinition Require(string? id)
    {
        var normalized = string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
        var preset = List().FirstOrDefault(x => string.Equals(x.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            throw new FileNotFoundException(
                $"Overlay preset '{normalized}' was not found. Rebuild the application package so Assets/overlay/presets is included.");
        }

        var directory = ResolveDirectory(preset.Id);
        foreach (var asset in new[] { preset.Template, preset.Stylesheet })
        {
            if (string.IsNullOrWhiteSpace(asset) || !File.Exists(Path.Combine(directory, asset)))
            {
                throw new FileNotFoundException(
                    $"Overlay preset '{preset.Id}' is incomplete. Missing resource: {asset}", directory);
            }
        }

        if (!string.IsNullOrWhiteSpace(preset.RequiredCssMarker))
        {
            var stylesheetPath = Path.Combine(directory, preset.Stylesheet);
            var stylesheet = File.ReadAllText(stylesheetPath);
            if (!stylesheet.Contains(preset.RequiredCssMarker, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Overlay preset '{preset.Id}' has the wrong stylesheet. " +
                    $"Expected CSS marker: {preset.RequiredCssMarker}");
            }
        }
        return preset;
    }

    public string ResolveDirectory(string id)
    {
        var definition = List().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(definition?.SourceDirectory))
        {
            return definition.SourceDirectory!;
        }

        var userPath = Path.Combine(UserDirectory, id);
        if (Directory.Exists(userPath))
        {
            return userPath;
        }

        return Path.Combine(BuiltInDirectory, id);
    }

    public string? ReadTemplate(string? id)
    {
        var definition = Get(id);
        return ReadAsset(definition, definition.Template);
    }

    public string? ReadStylesheet(string? id)
    {
        var definition = Get(id);
        return ReadAsset(definition, definition.Stylesheet);
    }

    public string? ReadScript(string? id)
    {
        var definition = Get(id);
        return string.IsNullOrWhiteSpace(definition.Script) ? null : ReadAsset(definition, definition.Script!);
    }

    public string? ReadRuntimeAsset(string fileName)
    {
        var runtimeDirectory = Path.Combine(AppPaths.BaseDirectory, "Assets", "overlay", "runtime");
        var fullPath = Path.GetFullPath(Path.Combine(runtimeDirectory, fileName));
        var root = Path.GetFullPath(runtimeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)
            ? File.ReadAllText(fullPath)
            : null;
    }

    public string CreateUserCopy(string id)
    {
        var source = ResolveDirectory(id);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var destination = Path.Combine(UserDirectory, id);
        Directory.CreateDirectory(UserDirectory);
        CopyDirectory(source, destination);
        return destination;
    }

    private void AddDefinitions(List<OverlayPresetDefinition> result, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var definition = JsonSerializer.Deserialize<OverlayPresetDefinition>(
                    File.ReadAllText(manifestPath), _jsonOptions);
                if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
                {
                    AppLogger.Error(
                        "Loading overlay preset manifest",
                        new InvalidDataException($"Manifest '{manifestPath}' does not contain a preset id."));
                    continue;
                }
                definition.SourceDirectory = directory;
                result.Add(definition);
            }
            catch (Exception exception)
            {
                AppLogger.Error($"Loading overlay preset manifest '{manifestPath}'", exception);
            }
        }
    }

    private string? ReadAsset(OverlayPresetDefinition definition, string fileName)
    {
        var directory = ResolveDirectory(definition.Id);
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return null;
        }

        return File.ReadAllText(fullPath);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
