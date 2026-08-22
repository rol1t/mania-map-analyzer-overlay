using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// One normalized frame of tosu v2 telemetry. All fields are optional because
/// tosu can emit partial packets while the game is changing state. Missing
/// numeric values remain null; they are never coerced to zero.
/// </summary>
public sealed record LiveTelemetryFrame
{
    public LiveTelemetryFrame(
        int mapTimeMs,
        bool isPaused,
        bool isFocused,
        bool isPlaying,
        LiveJudgementCounts judgements,
        int? score = null,
        double? accuracy = null,
        double? health = null,
        int? combo = null,
        int? maxCombo = null,
        double? unstableRate = null,
        IEnumerable<double>? hitErrorArray = null,
        bool failed = false,
        IEnumerable<string>? mods = null,
        bool isManiaSettings = false,
        double? bpm = null,
        double? hitWindow = null,
        bool isBreak = false,
        bool isKiai = false,
        string? rawPayloadHash = null,
        DateTimeOffset? capturedAt = null)
    {
        MapTimeMs = mapTimeMs;
        IsPaused = isPaused;
        IsFocused = isFocused;
        IsPlaying = isPlaying;
        Judgements = judgements ?? new LiveJudgementCounts();
        Score = score;
        Accuracy = accuracy;
        Health = health;
        Combo = combo;
        MaxCombo = maxCombo;
        UnstableRate = unstableRate;
        HitErrorArray = hitErrorArray?.ToImmutableArray() ?? ImmutableArray<double>.Empty;
        Failed = failed;
        Mods = mods?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        IsManiaSettings = isManiaSettings;
        Bpm = bpm;
        HitWindow = hitWindow;
        IsBreak = isBreak;
        IsKiai = isKiai;
        RawPayloadHash = rawPayloadHash;
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
    }

    public int MapTimeMs
    {
        get;
    }

    public bool IsPaused
    {
        get;
    }

    public bool IsFocused
    {
        get;
    }

    public bool IsPlaying
    {
        get;
    }

    public LiveJudgementCounts Judgements
    {
        get;
    }

    public int? Score
    {
        get;
    }

    public double? Accuracy
    {
        get;
    }

    public double? Health
    {
        get;
    }

    public int? Combo
    {
        get;
    }

    public int? MaxCombo
    {
        get;
    }

    public double? UnstableRate
    {
        get;
    }

    public ImmutableArray<double> HitErrorArray
    {
        get;
    }

    public bool Failed
    {
        get;
    }

    public ImmutableArray<string> Mods
    {
        get;
    }

    public bool IsManiaSettings
    {
        get;
    }

    public double? Bpm
    {
        get;
    }

    public double? HitWindow
    {
        get;
    }

    public bool IsBreak
    {
        get;
    }

    public bool IsKiai
    {
        get;
    }

    public string? RawPayloadHash
    {
        get;
    }

    public DateTimeOffset CapturedAt
    {
        get;
    }

    public int TotalJudgements => Judgements.Total;
}
