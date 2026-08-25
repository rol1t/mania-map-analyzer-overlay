using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Normalized diagnostic context for one raw Tosu v2 payload.
/// </summary>
public sealed record TosuRealtimeTelemetry(
    string Source,
    string RawStateName,
    int? RawStateNumber,
    bool? RawPaused,
    RealtimeTelemetrySample Sample,
    RealtimeAnalysisSnapshot Snapshot)
{
    public int JudgementTotal => Sample.Judgements.Total;

    public int HitErrorSampleCount => Sample.HitErrorArray.Length;
}

/// <summary>
/// Transport-neutral source for normalized Tosu realtime telemetry.
/// </summary>
public interface IRealtimeTelemetrySource
{
    Task<TosuRealtimeTelemetry?> ReadAsync(CancellationToken cancellationToken = default);

    void Reset();
}

/// <summary>Gameplay state projected from one raw Tosu v2 payload.</summary>
public sealed record TosuGameplayState(string Name, int? Number, bool? IsPlaying, bool? IsPaused);

/// <summary>
/// Native, visibility-independent Tosu v2 collector. It owns the raw-payload
/// boundary and feeds the single C# realtime analyzer. Missing fields in a
/// partial packet retain the last known value, while explicit zeroes and
/// counter/time decreases remain visible to retry detection.
/// </summary>
public sealed class TosuRealtimeCollector
{
    private readonly RealtimePlayAnalyzer _analyzer;
    private RealtimeTelemetrySample? _lastSample;
    private TosuRealtimeTelemetry? _lastTelemetry;
    private string _candidateBeatmapId = string.Empty;
    private int _candidateBeatmapObservations;

    public TosuRealtimeCollector(PauseCoachOptions? options = null)
    {
        _analyzer = new RealtimePlayAnalyzer(options);
    }

    public RealtimeAnalysisSnapshot? LastSnapshot => _analyzer.LastSnapshot;

    public TosuRealtimeTelemetry? Process(
        JsonElement payload,
        string source = "native-http",
        DateTimeOffset? receivedAt = null)
    {
        if (!TosuRealtimePayloadNormalizer.TryNormalize(payload, _lastSample, receivedAt ?? DateTimeOffset.UtcNow, out var normalized))
        {
            return null;
        }

        if (ShouldHoldTransientCarouselMap(normalized))
        {
            string candidateBeatmapId = normalized.Sample.BeatmapId.Trim();
            if (!string.Equals(_candidateBeatmapId, candidateBeatmapId, StringComparison.Ordinal))
            {
                _candidateBeatmapId = candidateBeatmapId;
                _candidateBeatmapObservations = 1;
            }
            else
            {
                _candidateBeatmapObservations++;
            }

            if (_candidateBeatmapObservations < 2 && _lastTelemetry is not null)
            {
                // Tosu can expose a carousel entry for one polling interval
                // while the selected map is still the previous one. Preserve
                // the last analyzed sample/session until the candidate is
                // observed twice; raw fields remain available for diagnostics.
                return _lastTelemetry with
                {
                    Source = source,
                    RawStateName = normalized.RawStateName,
                    RawStateNumber = normalized.RawStateNumber,
                    RawPaused = normalized.RawPaused
                };
            }

            _candidateBeatmapId = string.Empty;
            _candidateBeatmapObservations = 0;
        }
        else
        {
            _candidateBeatmapId = string.Empty;
            _candidateBeatmapObservations = 0;
        }

        _lastSample = normalized.Sample;
        var snapshot = _analyzer.Process(normalized.Sample);
        var telemetry = new TosuRealtimeTelemetry(
            source,
            normalized.RawStateName,
            normalized.RawStateNumber,
            normalized.RawPaused,
            normalized.Sample,
            snapshot);
        _lastTelemetry = telemetry;
        return telemetry;
    }

    public void Reset()
    {
        _lastSample = null;
        _lastTelemetry = null;
        _candidateBeatmapId = string.Empty;
        _candidateBeatmapObservations = 0;
        _analyzer.Reset();
    }

    private bool ShouldHoldTransientCarouselMap(TosuRealtimeNormalizedPayload normalized)
    {
        string stateToken = new string(normalized.RawStateName.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (stateToken is not "selectplay" and not "songselect")
        {
            return false;
        }

        RealtimeTelemetrySample sample = normalized.Sample;
        if (_lastSample is null || sample.State != RealtimePlayState.Menu)
        {
            return false;
        }

        string previousBeatmapId = _lastSample.BeatmapId.Trim();
        string currentBeatmapId = sample.BeatmapId.Trim();
        return !string.IsNullOrWhiteSpace(previousBeatmapId)
            && !string.IsNullOrWhiteSpace(currentBeatmapId)
            && !string.Equals(previousBeatmapId, currentBeatmapId, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TosuRealtimeNormalizedPayload(
    RealtimeTelemetrySample Sample,
    string RawStateName,
    int? RawStateNumber,
    bool? RawPaused);

public static class TosuRealtimePayloadNormalizer
{
    public static bool TryReadGameplayState(
        JsonElement payload,
        DateTimeOffset receivedAt,
        out TosuGameplayState gameplay)
    {
        gameplay = null!;
        if (!TryNormalize(payload, previous: null, receivedAt, out var normalized))
        {
            return false;
        }

        bool? isPlaying = normalized.Sample.State switch
        {
            RealtimePlayState.Playing or RealtimePlayState.Paused or RealtimePlayState.Replay or RealtimePlayState.Spectating => true,
            RealtimePlayState.Menu or RealtimePlayState.Results => false,
            _ when normalized.RawStateNumber == 2 => true,
            _ => null
        };
        bool? isPaused = normalized.Sample.State switch
        {
            RealtimePlayState.Paused => true,
            RealtimePlayState.Playing => false,
            RealtimePlayState.Menu or RealtimePlayState.Results => false,
            _ => normalized.RawPaused
        };
        gameplay = new TosuGameplayState(
            normalized.RawStateName,
            normalized.RawStateNumber,
            isPlaying,
            isPaused);
        return true;
    }

    public static bool TryNormalize(
        JsonElement payload,
        RealtimeTelemetrySample? previous,
        DateTimeOffset receivedAt,
        out TosuRealtimeNormalizedPayload normalized)
    {
        normalized = null!;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement state = Property(payload, "state");
        string rawStateName = StringValue(Property(state, "name")) ?? string.Empty;
        int? rawStateNumber = IntValue(Property(state, "number"));
        JsonElement game = Property(payload, "game");
        bool? rawPaused = BoolValue(Property(game, "paused")) ?? BoolValue(Property(game, "isPaused"));

        JsonElement beatmap = Property(payload, "beatmap");
        string? beatmapId = StringValue(Property(beatmap, "id")) ?? StringValue(Property(beatmap, "beatmapId"));
        string? beatmapHash = StringValue(Property(beatmap, "hash"))
            ?? StringValue(Property(beatmap, "md5"))
            ?? StringValue(Property(beatmap, "checksum"));
        bool mapChanged = previous is not null
            && !string.IsNullOrWhiteSpace(beatmapId)
            && !string.IsNullOrWhiteSpace(previous.BeatmapId)
            && !string.Equals(beatmapId, previous.BeatmapId, StringComparison.Ordinal);
        RealtimeTelemetrySample? prior = mapChanged ? null : previous;

        beatmapId ??= prior?.BeatmapId ?? string.Empty;
        beatmapHash ??= prior?.BeatmapHash;
        JsonElement time = Property(beatmap, "time");
        int mapTimeMs = IntValue(Property(time, "live")) ?? prior?.MapTimeMs ?? 0;

        JsonElement play = Property(payload, "play");
        int? score = IntOrPrevious(play, "score", prior?.Score);
        double? accuracy = NumberOrPrevious(play, "accuracy", prior?.Accuracy);
        if (accuracy is double accuracyValue && Math.Abs(accuracyValue) > 1.000001)
        {
            accuracy = accuracyValue / 100d;
        }

        JsonElement combo = Property(play, "combo");
        int? currentCombo = IntValue(combo) ?? IntOrPrevious(combo, "current", prior?.Combo);
        int? maxCombo = IntOrPrevious(combo, "max", prior?.MaxCombo);

        JsonElement health = Property(play, "healthBar");
        double? healthValue = NumberValue(Property(health, "normal"))
            ?? NumberValue(Property(health, "smooth"))
            ?? prior?.Health;

        LiveJudgementCounts judgements = ReadJudgements(Property(play, "hits"), prior?.Judgements);
        ImmutableArray<double> offsets = ReadOffsets(Property(play, "hitErrorArray"), prior?.HitErrorArray ?? ImmutableArray<double>.Empty);
        double? unstableRate = NumberOrPrevious(play, "unstableRate", prior?.UnstableRate);
        ImmutableArray<string> mods = ReadMods(Property(play, "mods"), prior?.Mods ?? ImmutableArray<string>.Empty);
        bool failed = BoolValue(Property(play, "failed")) ?? prior?.Failed ?? false;

        RealtimePlayState stateValue = NormalizeState(rawStateName, rawStateNumber, rawPaused, prior?.State ?? RealtimePlayState.Unknown);
        bool isReplay = stateValue == RealtimePlayState.Replay;
        bool isSpectating = stateValue == RealtimePlayState.Spectating;
        bool? focused = BoolValue(Property(game, "focused")) ?? prior?.Focused;

        var sample = new RealtimeTelemetrySample(
            beatmapId,
            stateValue,
            mapTimeMs,
            judgements,
            accuracy,
            score,
            currentCombo,
            maxCombo,
            healthValue,
            offsets,
            mods,
            beatmapHash,
            focused,
            failed,
            isReplay,
            isSpectating,
            AnalysisDataQuality.Reconstructed,
            receivedAt: receivedAt,
            unstableRate: unstableRate);

        normalized = new TosuRealtimeNormalizedPayload(sample, rawStateName, rawStateNumber, rawPaused);
        return true;
    }

    private static RealtimePlayState NormalizeState(
        string rawName,
        int? number,
        bool? paused,
        RealtimePlayState previous)
    {
        string token = new string(rawName.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (token is "replay" or "watchingreplay")
        {
            return RealtimePlayState.Replay;
        }

        if (token is "spectating" or "watching")
        {
            return RealtimePlayState.Spectating;
        }

        // Tosu can leave game.paused=true for a short time while transitioning
        // through song select/menu. A named non-gameplay state is stronger
        // evidence than that stale flag; otherwise a menu packet is
        // misclassified as Paused and the overlay never receives the state
        // change needed to reconcile its visibility.
        if (token is "result" or "results" or "resultscreen")
        {
            return RealtimePlayState.Results;
        }

        if (token is "menu" or "songselect" or "selectplay" or "selectedit" or "selectdrawings" or "edit" or "options" or "exit" or "lobby")
        {
            return RealtimePlayState.Menu;
        }

        if (paused == true || token is "pause" or "paused" or "break")
        {
            return RealtimePlayState.Paused;
        }

        if (token is "play" or "gameplay" or "playing")
        {
            return RealtimePlayState.Playing;
        }

        if (number == 2)
        {
            return paused == true ? RealtimePlayState.Paused : RealtimePlayState.Playing;
        }

        return previous;
    }

    private static LiveJudgementCounts ReadJudgements(JsonElement hits, LiveJudgementCounts? previous)
    {
        LiveJudgementCounts prior = previous ?? new LiveJudgementCounts();
        return new LiveJudgementCounts(
            IntOrPrevious(hits, "300", prior.Count300) ?? prior.Count300,
            IntOrPrevious(hits, "200", prior.Count200) ?? prior.Count200,
            IntOrPrevious(hits, "100", prior.Count100) ?? prior.Count100,
            IntOrPrevious(hits, "50", prior.Count50) ?? prior.Count50,
            IntOrPrevious(hits, "0", prior.CountMiss) ?? prior.CountMiss,
            IntOrPrevious(hits, "geki", prior.CountGeki),
            IntOrPrevious(hits, "katu", prior.CountKatu));
    }

    private static ImmutableArray<double> ReadOffsets(JsonElement value, ImmutableArray<double> previous)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return previous;
        }

        return value.EnumerateArray()
            .Select(NumberValue)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ReadMods(JsonElement value, ImmutableArray<string> previous)
    {
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return previous;
        }

        JsonElement arrayValue = Property(value, "array");
        if (arrayValue.ValueKind == JsonValueKind.Undefined)
        {
            // Tosu versions have exposed selected lazer mods as either
            // `array` or `list`; both are the same ordered collection.
            arrayValue = Property(value, "list");
        }
        if (arrayValue.ValueKind == JsonValueKind.Array)
        {
            value = arrayValue;
        }

        IEnumerable<string> values = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(ModValue)
            : value.ValueKind == JsonValueKind.Object && (Property(value, "acronym").ValueKind != JsonValueKind.Undefined || Property(value, "name").ValueKind != JsonValueKind.Undefined)
                ? new[] { ModValue(value) }
                : value.ValueKind == JsonValueKind.Object
                    ? value.EnumerateObject().Where(item => BoolValue(item.Value) == true).Select(item => item.Name)
                    : new[] { StringValue(value) ?? string.Empty };

        string[] normalized = values
            .Select(item => item.Trim().ToUpperInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 && value.ValueKind != JsonValueKind.Array ? previous : normalized.ToImmutableArray();
    }

    private static string ModValue(JsonElement value) => StringValue(Property(value, "acronym"))
        ?? StringValue(Property(value, "name"))
        ?? StringValue(value)
        ?? string.Empty;

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value;
        }

        return default;
    }

    private static string? StringValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.ToString();
        }

        return null;
    }

    private static int? IntValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer))
        {
            return integer;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? NumberValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return double.IsFinite(number) ? number : null;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return double.IsFinite(parsed) ? parsed : null;
        }

        return null;
    }

    private static bool? BoolValue(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? IntOrPrevious(JsonElement element, string name, int? previous) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? IntValue(value) ?? previous : previous;

    private static double? NumberOrPrevious(JsonElement element, string name, double? previous) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? NumberValue(value) ?? previous : previous;
}
