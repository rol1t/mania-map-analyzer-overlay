using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay;

/// <summary>
/// Loads all launcher-facing text from the external localization package.
/// The application code stores only stable keys; translators can edit the JSON
/// files in Assets/localization without recompiling the launcher.
/// </summary>
internal static class UiText
{
    private static readonly object Sync = new();
    private static Dictionary<string, Dictionary<string, string>>? resources;
    private static List<LanguageOption>? languages;
    private static Exception? loadError;
    private static string currentLanguage = DetectSystemLanguage();

    public static string CurrentLanguage
    {
        get
        {
            lock (Sync)
                return currentLanguage;
        }
    }

    public static bool IsEnglish => string.Equals(CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public static Exception? LoadError
    {
        get
        {
            lock (Sync)
                return loadError;
        }
    }

    public static IReadOnlyList<LanguageOption> Languages
    {
        get
        {
            EnsureLoaded();
            lock (Sync)
                return languages!.ToArray();
        }
    }

    public static void Initialize(string? languageId)
    {
        EnsureLoaded();
        lock (Sync)
        {
            var requested = languageId?.Trim();
            if (!string.IsNullOrWhiteSpace(requested) &&
                languages!.Any(language => string.Equals(language.Id, requested, StringComparison.OrdinalIgnoreCase)))
            {
                currentLanguage = languages!.First(language =>
                    string.Equals(language.Id, requested, StringComparison.OrdinalIgnoreCase)).Id;
            }
            else if (!languages!.Any(language => string.Equals(language.Id, currentLanguage, StringComparison.OrdinalIgnoreCase)))
            {
                currentLanguage = languages!.FirstOrDefault()?.Id ?? "en";
            }
        }
    }

    public static string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;
        EnsureLoaded();
        lock (Sync)
        {
            if (resources!.TryGetValue(currentLanguage, out var selected) && selected.TryGetValue(key, out var value))
                return value;
            if (resources.TryGetValue("en", out var fallback) && fallback.TryGetValue(key, out value))
                return value;
            return key;
        }
    }

    public static string Format(string key, params object?[] arguments)
    {
        var template = Get(key);
        return arguments.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    private static void EnsureLoaded()
    {
        if (resources is not null && languages is not null)
            return;
        lock (Sync)
        {
            if (resources is not null && languages is not null)
                return;
            try
            {
                var root = Path.Combine(AppPaths.BaseDirectory, "Assets", "localization");
                var manifestPath = Path.Combine(root, "manifest.json");
                if (!File.Exists(manifestPath))
                    throw new FileNotFoundException("Localization manifest is missing.", manifestPath);

                var manifest = JsonSerializer.Deserialize<LocalizationManifest>(
                    File.ReadAllText(manifestPath), JsonOptions) ?? throw new InvalidDataException("Localization manifest is empty.");
                if (manifest.Languages.Count == 0)
                    throw new InvalidDataException("Localization manifest does not define any languages.");

                var loadedLanguages = new List<LanguageOption>();
                var loadedResources = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in manifest.Languages)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.File))
                        throw new InvalidDataException("Every localization language must have an id and file.");
                    var languageFile = Path.Combine(root, entry.File);
                    if (!File.Exists(languageFile))
                        throw new FileNotFoundException($"Localization file for '{entry.Id}' is missing.", languageFile);
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(languageFile), JsonOptions) ?? throw new InvalidDataException($"Localization file '{languageFile}' is empty.");
                    loadedLanguages.Add(new LanguageOption(entry.Id.Trim(), entry.Name?.Trim() ?? entry.Id.Trim(), entry.File));
                    loadedResources[entry.Id.Trim()] = new Dictionary<string, string>(values, StringComparer.Ordinal);
                }

                resources = loadedResources;
                languages = loadedLanguages;
            }
            catch (Exception exception)
            {
                loadError = exception;
                AppLogger.Error("Loading localization resources", exception);
                resources = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                languages = new List<LanguageOption>();
            }
        }
    }

    private static string DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class LocalizationManifest
    {
        public List<LanguageManifestEntry> Languages { get; set; } = new();
    }

    private sealed class LanguageManifestEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
    }
}

internal sealed class LanguageOption
{
    public LanguageOption(string id, string name, string file)
    {
        Id = id;
        Name = name;
        File = file;
    }

    public string Id
    {
        get;
    }
    public string Name
    {
        get;
    }
    public string File
    {
        get;
    }
    public override string ToString() => Name;
}
