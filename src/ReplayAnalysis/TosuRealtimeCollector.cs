using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
/// Native, visibility-independent Tosu v2 collector. It owns the raw-payload
/// boundary and feeds the single C# realtime analyzer. Missing fields in a
/// partial packet retain the last known value, while explicit zeroes and
/// counter/time decreases remain visible to retry detection.
/// </summary>
public sealed class TosuRealtimeCollector
{
    private readonly RealtimePlayAnalyzer _analyzer;
    private RealtimeTelemetrySample? _lastSample;

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

        _lastSample = normalized.Sample;
        var snapshot = _analyzer.Process(normalized.Sample);
        return new TosuRealtimeTelemetry(
            source,
            normalized.RawStateName,
            normalized.RawStateNumber,
            normalized.RawPaused,
            normalized.Sample,
            snapshot);
    }

    public void Reset()
    {
        _lastSample = null;
        _analyzer.Reset();
    }
}

public sealed record TosuRealtimeNormalizedPayload(
    RealtimeTelemetrySample Sample,
    string RawStateName,
    int? RawStateNumber,
    bool? RawPaused);

public static class TosuRealtimePayloadNormalizer
{
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

        if (paused == true || token is "pause" or "paused" or "break")
        {
            return RealtimePlayState.Paused;
        }

        if (token is "play" or "gameplay" or "playing")
        {
            return RealtimePlayState.Playing;
        }

        if (token is "result" or "results" or "resultscreen")
        {
            return RealtimePlayState.Results;
        }

        if (token is "menu" or "songselect" or "selectplay" or "selectedit" or "selectdrawings" or "edit" or "options")
        {
            return RealtimePlayState.Menu;
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

    private static string? StringValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
