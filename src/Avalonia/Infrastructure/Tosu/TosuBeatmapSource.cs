using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Obtains raw beatmap input from tosu's HTTP API. It reads the v2 identity
/// before and after downloading the .osu file, retrying when the game changes
/// maps during the request.
/// </summary>
public sealed class TosuBeatmapSource : ITosuBeatmapSource
{
    private const string JsonV2Route = "json/v2";
    private const string BeatmapFileRoute = "files/beatmap/file";
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly int _maxConsistencyAttempts;

    public TosuBeatmapSource(
        HttpClient httpClient,
        Uri baseUri,
        IAnalysisDiagnostics? diagnostics = null,
        int maxConsistencyAttempts = 2)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The tosu base URI must be absolute.", nameof(baseUri));
        }

        if (maxConsistencyAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConsistencyAttempts),
                maxConsistencyAttempts,
                "At least one consistency attempt is required.");
        }

        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _diagnostics = diagnostics ?? new AppLoggerAnalysisDiagnostics();
        _maxConsistencyAttempts = maxConsistencyAttempts;
    }

    public async Task<TosuBeatmapSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _maxConsistencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var before = await ReadPayloadAsync(cancellationToken);
                var identityBefore = ExtractIdentity(before.Root);
                var rawBeatmap = await ReadBeatmapFileAsync(cancellationToken);
                var after = await ReadPayloadAsync(cancellationToken);
                var identityAfter = ExtractIdentity(after.Root);

                if (!IsConsistentIdentity(identityBefore, identityAfter))
                {
                    if (attempt < _maxConsistencyAttempts)
                    {
                        ReportWarning(
                            "tosu.beatmap_changed_during_fetch",
                            $"The beatmap changed while it was being fetched; retrying ({attempt}/{_maxConsistencyAttempts}).",
                            new Dictionary<string, string>
                            {
                                ["before"] = identityBefore.StableKey,
                                ["after"] = identityAfter.StableKey,
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture)
                            });
                        continue;
                    }

                    throw new TosuBeatmapSourceException(
                        $"The beatmap changed during all {_maxConsistencyAttempts} fetch attempts.");
                }

                return CreateSnapshot(identityBefore, before.Root, rawBeatmap);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TosuBeatmapSourceException exception)
            {
                ReportError("Reading current beatmap from tosu", exception);
                throw;
            }
            catch (Exception exception)
            {
                var wrapped = new TosuBeatmapSourceException(
                    "tosu did not provide a usable beatmap snapshot.",
                    exception);
                ReportError("Reading current beatmap from tosu", wrapped);
                throw wrapped;
            }
        }

        throw new InvalidOperationException("The tosu beatmap fetch loop completed unexpectedly.");
    }

    private async Task<TosuV2Payload> ReadPayloadAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(JsonV2Route, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TosuBeatmapSourceException("tosu returned a JSON payload that is not an object.");
            }

            return new TosuV2Payload(root);
        }
        catch (JsonException exception)
        {
            throw new TosuBeatmapSourceException("tosu returned malformed JSON from /json/v2.", exception);
        }
    }

    private async Task<string> ReadBeatmapFileAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(BeatmapFileRoute, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new TosuBeatmapSourceException("tosu returned an empty beatmap file.");
        }

        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(string route, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(new Uri(_baseUri, route), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new TosuBeatmapSourceException($"The tosu endpoint '{route}' could not be reached.", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var reason = response.ReasonPhrase;
            response.Dispose();
            throw new TosuBeatmapSourceException(
                $"The tosu endpoint '{route}' returned HTTP {status} ({reason}).");
        }

        return response;
    }

    private static TosuBeatmapSnapshot CreateSnapshot(
        BeatmapIdentity identity,
        JsonElement root,
        string rawBeatmap)
    {
        var source = SelectMapObject(root);
        var metadata = new TosuBeatmapMetadata
        {
            Artist = ReadString(source, "artist"),
            Title = ReadString(source, "title"),
            Version = ReadString(source, "version"),
            Mapper = FirstNonEmpty(ReadString(source, "mapper"), ReadString(source, "creator")),
            Bpm = ReadNumber(source, "bpm"),
            OverallDifficulty = ReadNumber(source, "overall_difficulty", "overallDifficulty", "od"),
            CircleSize = ReadNumber(source, "circle_size", "circleSize", "cs"),
            ApproachRate = ReadNumber(source, "approach_rate", "approachRate", "ar"),
            HealthDrain = ReadNumber(source, "hp_drain", "hpDrain", "health_drain", "healthDrain", "hp"),
            Mode = FirstNonEmpty(ReadString(source, "mode"), ReadScalarString(source, "mode_int")),
            BackgroundPath = FirstNonEmpty(
                ReadString(source, "background"),
                ReadString(source, "background_url"),
                ReadString(source, "backgroundUrl"))
        };

        var mods = ExtractMods(root);
        var rate = ExtractRate(root, mods);
        return new TosuBeatmapSnapshot(
            identity,
            rawBeatmap,
            metadata,
            rate,
            mods,
            DateTimeOffset.UtcNow);
    }

    private static BeatmapIdentity ExtractIdentity(JsonElement root)
    {
        var source = SelectMapObject(root);
        var id = FirstNonEmpty(
            ReadScalarString(source, "id"),
            ReadScalarString(root, "beatmap_id"),
            ReadScalarString(root, "beatmapId"));
        var hash = FirstNonEmpty(
            ReadString(source, "md5"),
            ReadString(source, "checksum"),
            ReadString(source, "hash"));
        var setId = FirstNonEmpty(
            ReadScalarString(source, "set"),
            ReadScalarString(source, "set_id"),
            ReadScalarString(source, "setId"),
            ReadScalarString(source, "beatmapset_id"),
            ReadScalarString(source, "beatmapSetId"));

        try
        {
            return new BeatmapIdentity(id, hash, setId);
        }
        catch (ArgumentException exception)
        {
            throw new TosuBeatmapSourceException(
                "tosu returned a payload without a current beatmap identity.",
                exception);
        }
    }

    private static bool IsConsistentIdentity(BeatmapIdentity before, BeatmapIdentity after)
    {
        var sharedDimension = false;
        if (!string.IsNullOrWhiteSpace(before.Id) && !string.IsNullOrWhiteSpace(after.Id))
        {
            sharedDimension = true;
            if (!string.Equals(before.Id, after.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(before.Hash) && !string.IsNullOrWhiteSpace(after.Hash))
        {
            sharedDimension = true;
            if (!string.Equals(before.Hash, after.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(before.SetId) && !string.IsNullOrWhiteSpace(after.SetId))
        {
            sharedDimension = true;
            if (!string.Equals(before.SetId, after.SetId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return sharedDimension;
    }

    private static JsonElement SelectMapObject(JsonElement root)
    {
        if (TryGetObject(root, out var beatmap, "beatmap") && HasIdentity(beatmap))
        {
            return beatmap;
        }

        if (TryGetObject(root, out var menu, "menu") && TryGetObject(menu, out var menuBeatmap, "bm"))
        {
            return menuBeatmap;
        }

        if (TryGetObject(root, out var play, "play") && TryGetObject(play, out var playBeatmap, "beatmap"))
        {
            return playBeatmap;
        }

        if (TryGetObject(root, out beatmap, "beatmap"))
        {
            return beatmap;
        }

        throw new TosuBeatmapSourceException("tosu returned a payload without beatmap metadata.");
    }

    private static bool HasIdentity(JsonElement value) =>
        !string.IsNullOrWhiteSpace(ReadScalarString(value, "id")) ||
        !string.IsNullOrWhiteSpace(ReadString(value, "md5", "checksum", "hash"));

    private static ImmutableArray<string> ExtractMods(JsonElement root)
    {
        var candidates = new List<JsonElement>();
        if (TryGetObject(root, out var play, "play") && TryGetProperty(play, out var playMods, "mods"))
        {
            candidates.Add(playMods);
        }

        if (TryGetObject(root, out var menu, "menu") && TryGetProperty(menu, out var menuMods, "mods"))
        {
            candidates.Add(menuMods);
        }

        if (TryGetObject(root, out var results, "resultsScreen") &&
            TryGetProperty(results, out var resultMods, "mods"))
        {
            candidates.Add(resultMods);
        }

        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            CollectModCodes(candidate, mods);
            if (mods.Count > 0)
            {
                break;
            }
        }

        return mods.OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static void CollectModCodes(JsonElement value, ISet<string> result)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectModCodes(item, result);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                AddModCodes(value.GetString() ?? string.Empty, result);
            }

            return;
        }

        foreach (var propertyName in new[] { "acronym", "str", "name" })
        {
            AddModCodes(ReadString(value, propertyName), result);
        }

        if (TryGetProperty(value, out var array, "array", "mods"))
        {
            CollectModCodes(array, result);
        }
    }

    private static void AddModCodes(string value, ISet<string> result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = new string(value
            .ToUpperInvariant()
            .Where(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            .ToArray());
        if (normalized is "NM" or "NOMOD" or "NONE")
        {
            return;
        }

        if (normalized.Length > 0)
        {
            result.Add(normalized);
        }
    }

    private static double ExtractRate(JsonElement root, ImmutableArray<string> mods)
    {
        foreach (var path in new[]
        {
            new[] { "play", "speedRate" },
            new[] { "play", "rate" },
            new[] { "game", "speedRate" },
            new[] { "game", "rate" },
            new[] { "rate" }
        })
        {
            if (TryGetPath(root, out var value, path) && TryReadNumber(value, out var rate) && rate > 0)
            {
                return rate;
            }
        }

        if (TryGetPath(root, out var play, "play") && TryGetProperty(play, out var playMods, "mods"))
        {
            var settingRate = FindSpeedChange(playMods);
            if (settingRate > 0)
            {
                return settingRate;
            }
        }

        if (mods.Contains("NC", StringComparer.OrdinalIgnoreCase) || mods.Contains("DT", StringComparer.OrdinalIgnoreCase))
        {
            return 1.5;
        }

        if (mods.Contains("HT", StringComparer.OrdinalIgnoreCase) || mods.Contains("DC", StringComparer.OrdinalIgnoreCase))
        {
            return 0.75;
        }

        return 1.0;
    }

    private static double FindSpeedChange(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var result = FindSpeedChange(item);
                if (result > 0)
                {
                    return result;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(value, out var settings, "settings") &&
                TryGetProperty(settings, out var speed, "speed_change", "speedChange") &&
                TryReadNumber(speed, out var rate) && rate > 0)
            {
                return rate;
            }

            if (TryGetProperty(value, out var nested, "array", "mods"))
            {
                return FindSpeedChange(nested);
            }
        }

        return 0;
    }

    private static bool TryGetPath(JsonElement value, out JsonElement result, params string[] path)
    {
        result = value;
        foreach (var segment in path)
        {
            if (!TryGetProperty(result, out result, segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetObject(JsonElement value, out JsonElement result, params string[] names)
    {
        if (TryGetProperty(value, out result, names) && result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement value, out JsonElement result, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out result))
                {
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static string ReadString(JsonElement value, params string[] names)
    {
        return TryGetProperty(value, out var property, names) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string ReadScalarString(JsonElement value, params string[] names)
    {
        if (!TryGetProperty(value, out var property, names))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static double? ReadNumber(JsonElement value, params string[] names)
    {
        return TryGetProperty(value, out var property, names) && TryReadNumber(property, out var number)
            ? number
            : null;
    }

    private static bool TryReadNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private void ReportWarning(string code, string message, IReadOnlyDictionary<string, string> properties)
    {
        _diagnostics.Report(new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Warning,
            code,
            message,
            properties: properties));
    }

    private void ReportError(string operation, TosuBeatmapSourceException exception)
    {
        _diagnostics.Report(AnalysisDiagnostic.Error(
            "tosu.beatmap_source_failed",
            exception.Message,
            exception,
            [new KeyValuePair<string, string>("operation", operation)]));
    }

    private sealed record TosuV2Payload(JsonElement Root);

    private sealed class AppLoggerAnalysisDiagnostics : IAnalysisDiagnostics
    {
        public void Report(AnalysisDiagnostic diagnostic)
        {
            var message = diagnostic.Properties.Count == 0
                ? diagnostic.Message
                : diagnostic.Message + " [" + string.Join(", ", diagnostic.Properties.Select(property =>
                    property.Key + "=" + property.Value)) + "]";
            var exception = string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails)
                ? null
                : new TosuBeatmapSourceException(diagnostic.TechnicalDetails);

            switch (diagnostic.Severity)
            {
                case AnalysisDiagnosticSeverity.Error:
                    AppLogger.Error(diagnostic.Code, exception ?? new TosuBeatmapSourceException(message));
                    break;
                case AnalysisDiagnosticSeverity.Warning:
                    AppLogger.Warning(diagnostic.Code, message, exception);
                    break;
                default:
                    AppLogger.Info(diagnostic.Code, message);
                    break;
            }
        }
    }
}
