using System.Collections.Immutable;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.RealtimeAnalysis;

/// <summary>Normalized gameplay state used by the realtime session tracker.</summary>
public enum RealtimePlayState
{
    Unknown,
    Menu,
    Playing,
    Paused,
    Results,
    Replay,
    Spectating
}

public enum RealtimeTelemetryEventType
{
    StateChanged,
    TelemetryReceived,
    AccuracyChanged,
    ComboChanged,
    MissCountChanged,
    JudgementCountChanged,
    HitErrorObserved,
    SectionChanged,
    PauseStarted,
    PauseEnded,
    RetryDetected,
    SessionFinished
}

/// <summary>All realtime thresholds live here instead of being scattered across rules.</summary>
public sealed record PauseCoachOptions
{
    public int RecentWindowSeconds { get; init; } = 20;

    public int BaselineWindowSeconds { get; init; } = 20;

    public int MinimumTimingSamples { get; init; } = 12;

    public double TimingBiasThresholdMs { get; init; } = 8;

    public double TimingInstabilityUrThreshold { get; init; } = 45;

    public double TimingInstabilityMultiplier { get; init; } = 1.35;

    public double AccuracyDropThreshold { get; init; } = 0.05;

    public double MissSpikeMultiplier { get; init; } = 2.0;

    public int MinimumMissesForSpike { get; init; } = 2;

    public double SectionAccuracyDropThreshold { get; init; } = 0.06;

    public int MaxTimelineEvents { get; init; } = 512;

    public int MaxTimingSamples { get; init; } = 512;

    public int MaxInsights { get; init; } = 4;

    public static PauseCoachOptions Default { get; } = new();
}

/// <summary>A normalized telemetry sample. Raw Tosu JSON never crosses this boundary.</summary>
public sealed record RealtimeTelemetrySample
{
    public RealtimeTelemetrySample(
        string beatmapId,
        RealtimePlayState state,
        int mapTimeMs,
        LiveJudgementCounts? judgements = null,
        double? accuracy = null,
        int? score = null,
        int? combo = null,
        int? maxCombo = null,
        double? health = null,
        IReadOnlyList<double>? hitErrorArray = null,
        IReadOnlyList<string>? mods = null,
        string? beatmapHash = null,
        bool? focused = null,
        bool failed = false,
        bool isReplay = false,
        bool isSpectating = false,
        AnalysisDataQuality timingQuality = AnalysisDataQuality.Reconstructed,
        PauseCoachSectionSnapshot? currentSection = null,
        IReadOnlyList<PauseCoachColumnSnapshot>? columns = null,
        DateTimeOffset? receivedAt = null,
        double? unstableRate = null)
    {
        BeatmapId = beatmapId ?? string.Empty;
        BeatmapHash = beatmapHash;
        State = state;
        MapTimeMs = mapTimeMs;
        Judgements = judgements ?? new LiveJudgementCounts();
        Accuracy = accuracy;
        Score = score;
        Combo = combo;
        MaxCombo = maxCombo;
        Health = health;
        UnstableRate = unstableRate;
        HitErrorArray = hitErrorArray?.ToImmutableArray() ?? ImmutableArray<double>.Empty;
        Mods = mods?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        Focused = focused;
        Failed = failed;
        IsReplay = isReplay;
        IsSpectating = isSpectating;
        TimingQuality = timingQuality;
        CurrentSection = currentSection;
        Columns = columns?.ToImmutableArray() ?? ImmutableArray<PauseCoachColumnSnapshot>.Empty;
        HasColumnTelemetry = columns is not null;
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
    }

    public string BeatmapId
    {
        get;
    }

    public string? BeatmapHash
    {
        get;
    }

    public RealtimePlayState State
    {
        get;
    }

    public int MapTimeMs
    {
        get;
    }

    public LiveJudgementCounts Judgements
    {
        get;
    }

    public double? Accuracy
    {
        get;
    }

    public int? Score
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

    public double? Health
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

    public ImmutableArray<string> Mods
    {
        get;
    }

    public bool? Focused
    {
        get;
    }

    public bool Failed
    {
        get;
    }

    public bool IsReplay
    {
        get;
    }

    public bool IsSpectating
    {
        get;
    }

    public AnalysisDataQuality TimingQuality
    {
        get;
    }

    public PauseCoachSectionSnapshot? CurrentSection
    {
        get;
    }

    public ImmutableArray<PauseCoachColumnSnapshot> Columns
    {
        get;
    }

    public bool HasColumnTelemetry
    {
        get;
    }

    public DateTimeOffset ReceivedAt
    {
        get;
    }
}

public sealed record RealtimeTelemetryEvent(
    int MapTimeMs,
    DateTimeOffset ReceivedAt,
    RealtimeTelemetryEventType Type,
    double? Value,
    string Source,
    AnalysisDataQuality Quality);

/// <summary>
/// Mutable owner of one attempt. Only <see cref="RealtimePlayAnalyzer"/> mutates it;
/// published snapshots contain copies of all collections.
/// </summary>
public sealed class RealtimePlaySession
{
    private readonly List<RealtimeTelemetryEvent> _timeline = [];

    internal RealtimePlaySession(string sessionId, RealtimeTelemetrySample firstSample)
    {
        SessionId = sessionId;
        BeatmapId = firstSample.BeatmapId;
        BeatmapHash = firstSample.BeatmapHash;
        StartedAt = firstSample.ReceivedAt;
        Mods = firstSample.Mods;
        Rate = 1d;
    }

    public string SessionId
    {
        get;
    }

    public string BeatmapId
    {
        get;
    }

    public string? BeatmapHash
    {
        get;
    }

    public DateTimeOffset StartedAt
    {
        get;
    }

    public DateTimeOffset? EndedAt
    {
        get; internal set;
    }

    public ImmutableArray<string> Mods
    {
        get; internal set;
    }

    public double Rate
    {
        get; internal set;
    }

    public IReadOnlyList<RealtimeTelemetryEvent> Timeline => _timeline.ToArray();

    internal void Add(RealtimeTelemetryEvent telemetryEvent, int maximum)
    {
        _timeline.Add(telemetryEvent);
        if (_timeline.Count > maximum)
        {
            _timeline.RemoveRange(0, _timeline.Count - maximum);
        }
    }
}

public sealed record RealtimeTimingStats(
    int SampleCount,
    double? MeanMs,
    double? MedianMs,
    double? StandardDeviationMs,
    double? UnstableRate,
    double? EarlyPercentage,
    double? LatePercentage,
    double? BaselineMeanMs,
    AnalysisDataQuality Quality)
{
    public double? BaselineUnstableRate
    {
        get; init;
    }
};

public sealed record RealtimePerformanceStats(
    double? Accuracy,
    double? BaselineAccuracy,
    int? Hits,
    int? Misses,
    int? RecentHits,
    int? RecentMisses,
    AnalysisDataQuality Quality)
{
    public double? RecentAccuracy
    {
        get; init;
    }
    public double? BaselineWindowAccuracy
    {
        get; init;
    }
};

public sealed record RealtimeAnalysisSnapshot(
    string SessionId,
    string BeatmapId,
    RealtimePlayState State,
    int MapTimeMs,
    DateTimeOffset UpdatedAt,
    RealtimeTimingStats Timing,
    RealtimePerformanceStats Performance,
    PauseCoachSectionSnapshot? CurrentSection,
    IReadOnlyList<PauseCoachColumnSnapshot> Columns,
    IReadOnlyList<PauseCoachSectionSnapshot> Sections,
    IReadOnlyList<PauseCoachInsightSnapshot> Insights,
    AnalysisDataQuality DataQuality,
    PauseCoachWidgetState WidgetState,
    bool HasEnoughData,
    IReadOnlyList<string> Diagnostics)
{
    public int? Score
    {
        get; init;
    }

    public double? Accuracy
    {
        get; init;
    }

    public double? Health
    {
        get; init;
    }

    public int? Combo
    {
        get; init;
    }

    public int? MaxCombo
    {
        get; init;
    }

    public double? UnstableRate
    {
        get; init;
    }

    public IReadOnlyList<string> Mods { get; init; } = Array.Empty<string>();

    public IReadOnlyList<double> RecentOffsets { get; init; } = Array.Empty<double>();
};

/// <summary>
/// Incremental, single-owner analyzer for one active attempt. It never reruns
/// the whole map and it deliberately emits no column/pattern claims without input.
/// </summary>
public sealed class RealtimePlayAnalyzer
{
    private readonly PauseCoachOptions _options;
    private readonly List<TimedOffset> _offsets = [];
    private readonly List<JudgementPoint> _judgements = [];
    private RealtimePlaySession? _session;
    private RealtimeTelemetrySample? _previousSample;
    private ImmutableArray<double> _previousHitErrorArray = ImmutableArray<double>.Empty;
    private RealtimeAnalysisSnapshot? _lastSnapshot;
    private int _sessionSequence;

    public RealtimePlayAnalyzer(PauseCoachOptions? options = null)
    {
        _options = options ?? PauseCoachOptions.Default;
    }

    public RealtimePlaySession? CurrentSession => _session;

    public RealtimeAnalysisSnapshot? LastSnapshot => _lastSnapshot;

    public RealtimeAnalysisSnapshot Process(RealtimeTelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.IsReplay || sample.IsSpectating || sample.State is RealtimePlayState.Replay or RealtimePlayState.Spectating)
        {
            EndCurrentSession(sample.ReceivedAt);
            return PublishUnavailable(sample, "Pause Coach is disabled for replay playback or spectating.");
        }

        bool retry = _session is not null && IsRetry(sample);
        bool beatmapChanged = _session is not null &&
            (!string.IsNullOrWhiteSpace(sample.BeatmapId) && !string.IsNullOrWhiteSpace(_session.BeatmapId)
                && !string.Equals(_session.BeatmapId, sample.BeatmapId, StringComparison.Ordinal)
             || !string.IsNullOrWhiteSpace(sample.BeatmapHash) && !string.IsNullOrWhiteSpace(_session.BeatmapHash)
                && !string.Equals(_session.BeatmapHash, sample.BeatmapHash, StringComparison.Ordinal));
        bool startsAfterFinishedState = sample.State == RealtimePlayState.Playing
            && _previousSample is not null
            && _previousSample.State is not RealtimePlayState.Playing and not RealtimePlayState.Paused;

        // A map/menu transition is a lifecycle boundary even when the next
        // packet is paused or only partially populated. Do not carry the old
        // session and timing window into the newly selected map.
        if (beatmapChanged || retry || startsAfterFinishedState || sample.State == RealtimePlayState.Menu)
        {
            if (_session is not null)
            {
                EndCurrentSession(sample.ReceivedAt);
            }

            ClearSessionState();
        }

        if (sample.State == RealtimePlayState.Playing && _session is null)
        {
            StartSession(sample);
        }

        if (_session is null
            && (sample.State is RealtimePlayState.Paused or RealtimePlayState.Results)
            && HasAttemptTelemetry(sample))
        {
            StartSession(sample);
        }

        if (_session is null)
        {
            _previousSample = sample;
            return PublishWaiting(sample);
        }

        var previous = _previousSample;
        AddStateEvents(previous, sample);
        AppendTelemetry(sample);
        _previousSample = sample;

        if (sample.State is RealtimePlayState.Menu or RealtimePlayState.Results || sample.Failed)
        {
            EndCurrentSession(sample.ReceivedAt);
        }

        RealtimeAnalysisSnapshot baseSnapshot = BuildSnapshot(sample);
        IReadOnlyList<PauseCoachInsightSnapshot> insights = sample.State == RealtimePlayState.Menu
            ? Array.Empty<PauseCoachInsightSnapshot>()
            : PauseCoachInsightEngine.Generate(baseSnapshot, _options);
        var snapshot = baseSnapshot with
        {
            Insights = insights
        };
        _lastSnapshot = snapshot;
        return snapshot;
    }

    public void Reset()
    {
        EndCurrentSession(DateTimeOffset.UtcNow);
        _session = null;
        _previousSample = null;
        _lastSnapshot = null;
        _offsets.Clear();
        _judgements.Clear();
        _previousHitErrorArray = ImmutableArray<double>.Empty;
    }

    private void StartSession(RealtimeTelemetrySample sample)
    {
        _sessionSequence++;
        _session = new RealtimePlaySession(
            $"{sample.BeatmapId}:{sample.ReceivedAt.UtcDateTime:yyyyMMddHHmmssfff}:{_sessionSequence}",
            sample);
        _offsets.Clear();
        _judgements.Clear();
        _previousHitErrorArray = ImmutableArray<double>.Empty;
        _previousSample = null;
        _session.Add(
            new RealtimeTelemetryEvent(sample.MapTimeMs, sample.ReceivedAt, RealtimeTelemetryEventType.StateChanged, null, "tosu.v2", AnalysisDataQuality.Observed),
            _options.MaxTimelineEvents);
    }

    private static bool HasAttemptTelemetry(RealtimeTelemetrySample sample)
    {
        // The overlay may be opened while osu! is already paused, so there
        // may be no preceding Playing sample to create the session.
        return !string.IsNullOrWhiteSpace(sample.BeatmapId)
            || !string.IsNullOrWhiteSpace(sample.BeatmapHash)
            || sample.MapTimeMs > 0
            || sample.Score is not null
            || sample.Accuracy is not null
            || sample.Judgements.Total > 0;
    }

    private void EndCurrentSession(DateTimeOffset endedAt)
    {
        if (_session is null)
        {
            return;
        }

        _session.EndedAt = endedAt;
        _session.Add(
            new RealtimeTelemetryEvent(0, endedAt, RealtimeTelemetryEventType.SessionFinished, null, "realtime", AnalysisDataQuality.Observed),
            _options.MaxTimelineEvents);
    }

    private void ClearSessionState()
    {
        _session = null;
        _previousSample = null;
        _offsets.Clear();
        _judgements.Clear();
        _previousHitErrorArray = ImmutableArray<double>.Empty;
    }

    private bool IsRetry(RealtimeTelemetrySample sample)
    {
        if (_previousSample is null)
        {
            return false;
        }

        bool mapTimeRewound = sample.MapTimeMs + 1500 < _previousSample.MapTimeMs;
        if (!mapTimeRewound)
        {
            // Score and cumulative judgement counters can be reconstructed
            // from different Tosu packets and may briefly move backwards
            // during normal play. They are retry evidence only together with
            // a map-time rewind.
            return false;
        }

        bool scoreReset = sample.Score.HasValue
            && _previousSample.Score.HasValue
            && sample.Score.Value + 100 < _previousSample.Score.Value;
        bool missCounterReset = sample.Judgements.CountMiss + 1 < _previousSample.Judgements.CountMiss;
        bool hitCounterReset = sample.Judgements.HitTotal + 2 < _previousSample.Judgements.HitTotal;
        // Some retry packets omit score/hits entirely; a rewind to the very
        // beginning is still positive lifecycle evidence in that case.
        return scoreReset || missCounterReset || hitCounterReset || sample.MapTimeMs <= 1000;
    }

    private void AddStateEvents(RealtimeTelemetrySample? previous, RealtimeTelemetrySample current)
    {
        if (_session is null)
        {
            return;
        }

        if (previous is null || previous.State != current.State)
        {
            _session.Add(
                new RealtimeTelemetryEvent(current.MapTimeMs, current.ReceivedAt, RealtimeTelemetryEventType.StateChanged, null, "tosu.v2", AnalysisDataQuality.Observed),
                _options.MaxTimelineEvents);
            if (current.State == RealtimePlayState.Paused)
            {
                _session.Add(
                    new RealtimeTelemetryEvent(current.MapTimeMs, current.ReceivedAt, RealtimeTelemetryEventType.PauseStarted, null, "tosu.v2", AnalysisDataQuality.Observed),
                    _options.MaxTimelineEvents);
            }
            else if (previous?.State == RealtimePlayState.Paused && current.State == RealtimePlayState.Playing)
            {
                _session.Add(
                    new RealtimeTelemetryEvent(current.MapTimeMs, current.ReceivedAt, RealtimeTelemetryEventType.PauseEnded, null, "tosu.v2", AnalysisDataQuality.Observed),
                    _options.MaxTimelineEvents);
            }
        }

        if (previous is not null && previous.Judgements.CountMiss != current.Judgements.CountMiss)
        {
            _session.Add(
                new RealtimeTelemetryEvent(current.MapTimeMs, current.ReceivedAt, RealtimeTelemetryEventType.MissCountChanged, current.Judgements.CountMiss, "tosu.v2", AnalysisDataQuality.Observed),
                _options.MaxTimelineEvents);
        }
    }

    private void AppendTelemetry(RealtimeTelemetrySample sample)
    {
        if (_session is null)
        {
            return;
        }

        _session.Add(
            new RealtimeTelemetryEvent(sample.MapTimeMs, sample.ReceivedAt, RealtimeTelemetryEventType.TelemetryReceived, null, "tosu.v2", AnalysisDataQuality.Observed),
            _options.MaxTimelineEvents);

        if (sample.Accuracy.HasValue)
        {
            _session.Add(
                new RealtimeTelemetryEvent(sample.MapTimeMs, sample.ReceivedAt, RealtimeTelemetryEventType.AccuracyChanged, sample.Accuracy, "tosu.v2", AnalysisDataQuality.Observed),
                _options.MaxTimelineEvents);
        }

        if (!sample.HitErrorArray.IsDefaultOrEmpty)
        {
            AppendNewOffsets(sample.HitErrorArray, sample.ReceivedAt, sample.MapTimeMs);
        }

        _judgements.Add(new JudgementPoint(sample.MapTimeMs, sample.Judgements));
        if (_judgements.Count > _options.MaxTimelineEvents)
        {
            _judgements.RemoveRange(0, _judgements.Count - _options.MaxTimelineEvents);
        }

        // Keep the longest cumulative prefix through a transient truncation.
        // A following full payload can then append only truly new offsets
        // instead of re-adding the truncated tail.
        if (!IsStrictPrefix(sample.HitErrorArray, _previousHitErrorArray))
        {
            _previousHitErrorArray = sample.HitErrorArray;
        }
    }

    private void AppendNewOffsets(ImmutableArray<double> current, DateTimeOffset receivedAt, int mapTimeMs)
    {
        int commonPrefix = 0;
        int comparable = Math.Min(current.Length, _previousHitErrorArray.Length);
        while (commonPrefix < comparable && Math.Abs(current[commonPrefix] - _previousHitErrorArray[commonPrefix]) < 0.001)
        {
            commonPrefix++;
        }

        // Tosu can truncate the cumulative array on a state transition. A
        // shorter exact prefix contains no new hits; treating it as a reset
        // duplicates every old offset and makes UR/bias jump.
        int start = commonPrefix == current.Length
            ? current.Length
            : commonPrefix == _previousHitErrorArray.Length
                ? commonPrefix
                : 0;
        for (int index = start; index < current.Length; index++)
        {
            _offsets.Add(new TimedOffset(receivedAt, mapTimeMs, current[index]));
        }

        if (_offsets.Count > _options.MaxTimingSamples)
        {
            _offsets.RemoveRange(0, _offsets.Count - _options.MaxTimingSamples);
        }
    }

    private static bool IsStrictPrefix(ImmutableArray<double> prefix, ImmutableArray<double> value)
    {
        if (prefix.Length >= value.Length)
        {
            return false;
        }

        for (int index = 0; index < prefix.Length; index++)
        {
            if (Math.Abs(prefix[index] - value[index]) >= 0.001)
            {
                return false;
            }
        }

        return true;
    }

    private RealtimeAnalysisSnapshot BuildSnapshot(RealtimeTelemetrySample sample)
    {
        int recentCutoff = sample.MapTimeMs - _options.RecentWindowSeconds * 1000;
        int baselineCutoff = recentCutoff - _options.BaselineWindowSeconds * 1000;
        double[] recentOffsets = _offsets.Where(offset => offset.MapTimeMs >= recentCutoff && offset.MapTimeMs <= sample.MapTimeMs).Select(offset => offset.Value).ToArray();
        double[] baselineOffsets = _offsets.Where(offset => offset.MapTimeMs < recentCutoff && offset.MapTimeMs >= baselineCutoff).Select(offset => offset.Value).ToArray();
        RealtimeTimingStats timing = BuildTiming(recentOffsets, baselineOffsets, sample.TimingQuality);

        LiveJudgementCounts recentStart = FindPreviousCounts(recentCutoff);
        LiveJudgementCounts baselineStart = FindPreviousCounts(baselineCutoff);
        LiveJudgementCounts delta = sample.Judgements.DeltaFrom(recentStart);
        LiveJudgementCounts baselineDelta = recentStart.DeltaFrom(baselineStart);
        double? recentAccuracy = AccuracyFromCounts(delta);
        double? baselineAccuracy = AccuracyFromCounts(baselineDelta);
        RealtimePerformanceStats performance = new(
            sample.Accuracy,
            baselineAccuracy,
            sample.Judgements.HitTotal,
            sample.Judgements.CountMiss,
            delta.HitTotal,
            delta.CountMiss,
            AnalysisDataQuality.Observed)
        {
            RecentAccuracy = recentAccuracy,
            BaselineWindowAccuracy = baselineAccuracy
        };

        var sections = sample.CurrentSection is null
            ? Array.Empty<PauseCoachSectionSnapshot>()
            : new[] { sample.CurrentSection };
        AnalysisDataQuality quality = DetermineQuality(timing.Quality, performance.Quality, sample.HasColumnTelemetry, sample.CurrentSection is not null);
        bool enough = timing.SampleCount >= _options.MinimumTimingSamples
            || performance.RecentHits.HasValue
            && performance.RecentHits.Value > 0
            && sample.MapTimeMs >= _options.RecentWindowSeconds * 1000;
        PauseCoachWidgetState widgetState = sample.State switch
        {
            RealtimePlayState.Paused => enough ? PauseCoachWidgetState.Paused : PauseCoachWidgetState.InsufficientData,
            RealtimePlayState.Results => enough ? PauseCoachWidgetState.Ready : PauseCoachWidgetState.InsufficientData,
            RealtimePlayState.Playing => PauseCoachWidgetState.Playing,
            _ => PauseCoachWidgetState.WaitingForGame
        };

        var diagnostics = new List<string>();
        if (timing.SampleCount < _options.MinimumTimingSamples)
        {
            diagnostics.Add($"pausecoach.insufficient_timing: need {_options.MinimumTimingSamples} timing samples, have {timing.SampleCount}.");
        }
        if (!sample.HasColumnTelemetry)
        {
            diagnostics.Add("pausecoach.columns.unavailable: Tosu v2 payload does not expose reliable key-to-note correlation.");
        }
        if (sample.CurrentSection is null)
        {
            diagnostics.Add("pausecoach.patterns.unavailable: canonical realtime section/pattern data was not provided by the adapter.");
        }

        return new RealtimeAnalysisSnapshot(
            _session?.SessionId ?? string.Empty,
            sample.BeatmapId,
            sample.State,
            sample.MapTimeMs,
            sample.ReceivedAt,
            timing,
            performance,
            sample.CurrentSection,
            sample.HasColumnTelemetry ? sample.Columns : Array.Empty<PauseCoachColumnSnapshot>(),
            sections,
            Array.Empty<PauseCoachInsightSnapshot>(),
            quality,
            widgetState,
            enough,
            diagnostics)
        {
            Score = sample.Score,
            Accuracy = sample.Accuracy,
            Health = sample.Health,
            Combo = sample.Combo,
            MaxCombo = sample.MaxCombo,
            UnstableRate = sample.UnstableRate,
            Mods = sample.Mods,
            RecentOffsets = recentOffsets.TakeLast(20).ToArray()
        };
    }

    private LiveJudgementCounts FindPreviousCounts(int cutoffMapTimeMs)
    {
        // Find the cumulative counter at the start of the gameplay-time window,
        // not merely the previous websocket packet. This keeps Recent 20s
        // stable across different Tosu publication rates and wall-clock pauses.
        JudgementPoint? point = _judgements
            .Where(candidate => candidate.MapTimeMs <= cutoffMapTimeMs)
            .OrderBy(candidate => candidate.MapTimeMs)
            .LastOrDefault();
        return point?.Counts ?? (_judgements.Count > 0 ? _judgements[0].Counts : new LiveJudgementCounts());
    }

    private RealtimeAnalysisSnapshot PublishWaiting(RealtimeTelemetrySample sample)
    {
        var snapshot = new RealtimeAnalysisSnapshot(
            string.Empty,
            sample.BeatmapId,
            sample.State,
            sample.MapTimeMs,
            sample.ReceivedAt,
            new RealtimeTimingStats(0, null, null, null, null, null, null, null, AnalysisDataQuality.Unavailable),
            new RealtimePerformanceStats(sample.Accuracy, null, sample.Judgements.HitTotal, sample.Judgements.CountMiss, null, null, AnalysisDataQuality.Observed),
            null,
            Array.Empty<PauseCoachColumnSnapshot>(),
            Array.Empty<PauseCoachSectionSnapshot>(),
            Array.Empty<PauseCoachInsightSnapshot>(),
            AnalysisDataQuality.Unavailable,
            PauseCoachWidgetState.WaitingForGame,
            false,
            ["pausecoach.waiting: start a mania play to collect realtime telemetry."])
        {
            Score = sample.Score,
            Accuracy = sample.Accuracy,
            Health = sample.Health,
            Combo = sample.Combo,
            MaxCombo = sample.MaxCombo,
            UnstableRate = sample.UnstableRate,
            Mods = sample.Mods,
            RecentOffsets = sample.HitErrorArray.TakeLast(20).ToArray()
        };
        _lastSnapshot = snapshot;
        return snapshot;
    }

    private RealtimeAnalysisSnapshot PublishUnavailable(RealtimeTelemetrySample sample, string reason)
    {
        var snapshot = new RealtimeAnalysisSnapshot(
            string.Empty,
            sample.BeatmapId,
            sample.State,
            sample.MapTimeMs,
            sample.ReceivedAt,
            new RealtimeTimingStats(0, null, null, null, null, null, null, null, AnalysisDataQuality.Unavailable),
            new RealtimePerformanceStats(null, null, null, null, null, null, AnalysisDataQuality.Unavailable),
            null,
            Array.Empty<PauseCoachColumnSnapshot>(),
            Array.Empty<PauseCoachSectionSnapshot>(),
            Array.Empty<PauseCoachInsightSnapshot>(),
            AnalysisDataQuality.Unavailable,
            PauseCoachWidgetState.Unavailable,
            false,
            [reason])
        {
            Score = sample.Score,
            Accuracy = sample.Accuracy,
            Health = sample.Health,
            Combo = sample.Combo,
            MaxCombo = sample.MaxCombo,
            UnstableRate = sample.UnstableRate,
            Mods = sample.Mods,
            RecentOffsets = sample.HitErrorArray.TakeLast(20).ToArray()
        };
        _lastSnapshot = snapshot;
        return snapshot;
    }

    private static RealtimeTimingStats BuildTiming(double[] recent, double[] baseline, AnalysisDataQuality quality)
    {
        if (recent.Length == 0)
        {
            return new RealtimeTimingStats(0, null, null, null, null, null, null, MeanOrNull(baseline), AnalysisDataQuality.Unavailable)
            {
                BaselineUnstableRate = UnstableRate(baseline)
            };
        }

        double mean = recent.Average();
        double variance = recent.Select(value => Math.Pow(value - mean, 2)).Average();
        double deviation = Math.Sqrt(variance);
        int earlyCount = recent.Count(value => value < 0);
        int lateCount = recent.Count(value => value > 0);
        return new RealtimeTimingStats(
            recent.Length,
            mean,
            Median(recent),
            deviation,
            deviation * 10,
            earlyCount * 100d / recent.Length,
            lateCount * 100d / recent.Length,
            MeanOrNull(baseline),
            quality == AnalysisDataQuality.Exact ? AnalysisDataQuality.Exact : AnalysisDataQuality.Reconstructed)
        {
            BaselineUnstableRate = UnstableRate(baseline)
        };
    }

    private static double? UnstableRate(double[] values)
    {
        if (values.Length == 0)
        {
            return null;
        }

        double mean = values.Average();
        double variance = values.Select(value => Math.Pow(value - mean, 2)).Average();
        return Math.Sqrt(variance) * 10;
    }

    private static double? AccuracyFromCounts(LiveJudgementCounts counts)
    {
        if (counts.Total <= 0 || !counts.CountGeki.HasValue || !counts.CountKatu.HasValue)
        {
            return null;
        }

        int max = counts.Count300 + counts.CountGeki.Value;
        int twoHundred = counts.Count200 + counts.CountKatu.Value;
        double weighted = max * 6d + twoHundred * 4d + counts.Count100 * 2d + counts.Count50;
        return weighted / (counts.Total * 6d);
    }

    private static AnalysisDataQuality DetermineQuality(AnalysisDataQuality timing, AnalysisDataQuality performance, bool hasColumns, bool hasSection)
    {
        if (timing == AnalysisDataQuality.Unavailable && performance == AnalysisDataQuality.Unavailable)
        {
            return AnalysisDataQuality.Unavailable;
        }
        if (timing == AnalysisDataQuality.Reconstructed || hasSection || hasColumns)
        {
            return AnalysisDataQuality.Reconstructed;
        }
        return performance;
    }

    private static double? MeanOrNull(IEnumerable<double> values)
    {
        double[] materialized = values.ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    private static double Median(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private readonly record struct TimedOffset(DateTimeOffset ReceivedAt, int MapTimeMs, double Value);

    private readonly record struct JudgementPoint(int MapTimeMs, LiveJudgementCounts Counts);
}

/// <summary>Pure, deterministic and evidence-first insight rules.</summary>
public static class PauseCoachInsightEngine
{
    public static IReadOnlyList<PauseCoachInsightSnapshot> Generate(
        RealtimeAnalysisSnapshot snapshot,
        PauseCoachOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= PauseCoachOptions.Default;

        var candidates = new List<InsightCandidate>();
        RealtimeTimingStats timing = snapshot.Timing;
        RealtimePerformanceStats performance = snapshot.Performance;

        if (!snapshot.HasEnoughData || timing.SampleCount < options.MinimumTimingSamples && (performance.RecentHits ?? 0) < options.MinimumTimingSamples)
        {
            candidates.Add(new InsightCandidate(
                PauseCoachInsightType.InsufficientData,
                PauseCoachInsightSeverity.Info,
                "Not enough telemetry yet",
                "Keep playing a little longer so Pause Coach can separate a real trend from noise.",
                $"Timing samples: {timing.SampleCount}/{options.MinimumTimingSamples}; recent judgement delta unavailable.",
                0.9,
                AnalysisDataQuality.Unavailable));
        }

        if (timing.SampleCount >= options.MinimumTimingSamples && timing.MeanMs is double mean && Math.Abs(mean) >= options.TimingBiasThresholdMs)
        {
            bool late = mean > 0;
            candidates.Add(new InsightCandidate(
                late ? PauseCoachInsightType.TimingLate : PauseCoachInsightType.TimingEarly,
                PauseCoachInsightSeverity.Warning,
                late ? "Consistent late timing" : "Consistent early timing",
                $"Your recent hits are centered {Math.Abs(mean):F1} ms {(late ? "late" : "early")}.",
                $"Recent mean: {mean:+0.0;-0.0;0.0} ms; samples: {timing.SampleCount}.",
                Math.Clamp(0.58 + Math.Min(0.25, timing.SampleCount / 200d), 0, 0.95),
                timing.Quality));
        }

        if (timing.SampleCount >= options.MinimumTimingSamples && timing.UnstableRate is double ur && ur >= options.TimingInstabilityUrThreshold)
        {
            bool worsened = timing.BaselineUnstableRate is double baselineUr
                && baselineUr > 0
                && ur >= baselineUr * options.TimingInstabilityMultiplier;
            candidates.Add(new InsightCandidate(
                PauseCoachInsightType.TimingUnstable,
                worsened ? PauseCoachInsightSeverity.Critical : PauseCoachInsightSeverity.Warning,
                worsened ? "Timing stability dropped" : "Timing is unstable",
                "Your timing spread is wide enough to explain a noticeable accuracy loss.",
                $"Recent UR: {ur:F1}; baseline UR: {Format(timing.BaselineUnstableRate)}; samples: {timing.SampleCount}.",
                0.62,
                timing.Quality));
        }

        if (performance.RecentAccuracy is double accuracy && performance.BaselineWindowAccuracy is double baseline &&
            baseline - accuracy >= options.AccuracyDropThreshold)
        {
            candidates.Add(new InsightCandidate(
                PauseCoachInsightType.AccuracyDrop,
                PauseCoachInsightSeverity.Critical,
                "Accuracy dropped recently",
                "The latest section is performing below your earlier baseline.",
                $"Current: {accuracy:P1}; baseline: {baseline:P1}; delta: {accuracy - baseline:+0.0%;-0.0%;0.0%}.",
                0.72,
                performance.Quality));
        }

        if (performance.RecentMisses is int recentMisses && recentMisses >= options.MinimumMissesForSpike)
        {
            candidates.Add(new InsightCandidate(
                PauseCoachInsightType.MissSpike,
                recentMisses >= options.MinimumMissesForSpike * 2 ? PauseCoachInsightSeverity.Critical : PauseCoachInsightSeverity.Warning,
                "Misses spiked in the recent window",
                "Most of the current damage happened recently, not evenly across the attempt.",
                $"Recent misses: {recentMisses}; recent hits: {performance.RecentHits?.ToString() ?? "—"}.",
                0.6,
                performance.Quality));
        }

        PauseCoachSectionSnapshot? section = snapshot.CurrentSection;
        if (section?.AccuracyDelta is double sectionDelta && sectionDelta <= -options.SectionAccuracyDropThreshold)
        {
            candidates.Add(new InsightCandidate(
                PauseCoachInsightType.SectionCollapse,
                PauseCoachInsightSeverity.Warning,
                "The current section is the weak point",
                $"Performance fell in {section.Label}.",
                $"Section accuracy delta: {sectionDelta:+0.0%;-0.0%;0.0%}; data: {section.DataQuality}.",
                0.55,
                ParseQuality(section.DataQuality)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Severity)
            .ThenByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Type)
            .GroupBy(candidate => candidate.Type)
            .Select(group => ToSnapshot(group.First()))
            .Take(Math.Max(1, options.MaxInsights))
            .ToArray();
    }

    private static PauseCoachInsightSnapshot ToSnapshot(InsightCandidate candidate)
    {
        string confidence = candidate.Confidence >= 0.75 ? "high" : candidate.Confidence >= 0.55 ? "medium" : "low";
        string type = candidate.Type.ToString();
        return new PauseCoachInsightSnapshot
        {
            Code = $"pausecoach.{type.ToLowerInvariant()}",
            Type = type,
            Severity = candidate.Severity.ToString(),
            Title = candidate.Title,
            Description = candidate.Description,
            Evidence = candidate.Evidence,
            Message = $"{candidate.Description} {candidate.Evidence}",
            Confidence = candidate.Confidence,
            ConfidenceLabel = confidence,
            DataQuality = candidate.Quality.ToString()
        };
    }

    private static string Format(double? value) => value.HasValue
        ? value.Value.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    private static AnalysisDataQuality ParseQuality(string value) =>
        Enum.TryParse<AnalysisDataQuality>(value, ignoreCase: true, out var result) ? result : AnalysisDataQuality.Unavailable;

    private sealed record InsightCandidate(
        PauseCoachInsightType Type,
        PauseCoachInsightSeverity Severity,
        string Title,
        string Description,
        string Evidence,
        double Confidence,
        AnalysisDataQuality Quality);
}

public static class PauseCoachSnapshotMapper
{
    public static PauseCoachSnapshot ToSnapshot(RealtimeAnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PauseCoachSnapshot result = new()
        {
            State = snapshot.WidgetState.ToString(),
            SessionId = snapshot.SessionId,
            DataQuality = snapshot.DataQuality.ToString(),
            UpdatedAt = snapshot.UpdatedAt,
            Fidelity = "provisional",
            IsProvisional = true,
            Reason = "Realtime Tosu v2 telemetry. Exact per-column and per-object information requires an .osr replay.",
            MapProgressMs = snapshot.MapTimeMs,
            Score = snapshot.Score,
            Accuracy = snapshot.Accuracy,
            Health = snapshot.Health,
            Combo = snapshot.Combo,
            MaxCombo = snapshot.MaxCombo,
            Failed = false,
            Mods = snapshot.Mods,
            Timing = new PauseCoachTimingSnapshot
            {
                SampleCount = snapshot.Timing.SampleCount,
                MeanMs = snapshot.Timing.MeanMs,
                MedianMs = snapshot.Timing.MedianMs,
                UnstableRate = snapshot.Timing.UnstableRate,
                EarlyLateRatio = snapshot.Timing.EarlyPercentage is double early && snapshot.Timing.LatePercentage is double late && late > 0 ? early / late : null,
                DriftMs = snapshot.Timing.MeanMs,
                PreviousBaselineMs = snapshot.Timing.BaselineMeanMs,
                TimingMargin = snapshot.Timing.SampleCount == 0 ? "unknown" : "observed",
                RecentOffsets = snapshot.RecentOffsets,
                DataQuality = snapshot.Timing.Quality.ToString()
            },
            Overall = new PauseCoachOverallSnapshot
            {
                Accuracy = snapshot.Accuracy,
                Combo = snapshot.Combo,
                MaxCombo = snapshot.MaxCombo,
                Score = snapshot.Score,
                Hits = snapshot.Performance.Hits,
                Misses = snapshot.Performance.Misses,
                DataQuality = snapshot.Performance.Quality.ToString()
            },
            Recent = new PauseCoachRecentSnapshot
            {
                // The public snapshot contract currently exposes a fixed
                // twenty-second recent window.
                WindowSeconds = 20,
                Accuracy = snapshot.Performance.RecentAccuracy,
                MeanTimingMs = snapshot.Timing.MeanMs,
                TimingDeviationMs = snapshot.Timing.StandardDeviationMs,
                Hits = snapshot.Performance.RecentHits,
                Misses = snapshot.Performance.RecentMisses,
                DataQuality = snapshot.DataQuality.ToString()
            },
            Performance = new PauseCoachPerformanceSnapshot
            {
                WholeHits = snapshot.Performance.Hits,
                WholeMisses = snapshot.Performance.Misses,
                WholeAccuracy = snapshot.Accuracy,
                RecentAccuracy = snapshot.Performance.RecentAccuracy,
                RecentHits = snapshot.Performance.RecentHits,
                RecentMisses = snapshot.Performance.RecentMisses
            },
            Section = snapshot.CurrentSection ?? new PauseCoachSectionSnapshot(),
            Columns = snapshot.Columns,
            Sections = snapshot.Sections,
            Insights = snapshot.Insights,
            Diagnostics = snapshot.Diagnostics
        };
        return result;
    }
}
