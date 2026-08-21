using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Provisional live source that records only documented tosu telemetry:
/// beatmap.time.live (map progress), aggregate score/judgements and numeric
/// hit offsets. It never emits per-column, exact object offsets, LN release
/// or pattern conclusions. All outputs are marked Provisional with a reason.
/// Bounded buffers and background processing guarantee the overlay cannot affect gameplay.
/// </summary>
public sealed class TosuLiveReplaySource : IReplaySource
{
    public const int DefaultCapacity = 1024;
    private readonly BoundedReplayBuffer _buffer;
    private readonly ReplayProvenance _provenance;

    public TosuLiveReplaySource(int capacity = DefaultCapacity, string rulesetVersion = "provisional-1.0")
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new BoundedReplayBuffer(capacity);
        _provenance = new ReplayProvenance(ReplaySourceKind.ProvisionalLive, ReplayAnalysisFidelity.Provisional, "mania", rulesetVersion, reason: "Live telemetry is provisional; per-column/LN require replay file.");
        Kind = ReplaySourceKind.ProvisionalLive;
    }

    public ReplaySourceKind Kind
    {
        get;
    }

    public ReplayProvenance Provenance => _provenance;

    public int BufferedCount => _buffer.Count;

    public void RecordLiveFrame(TosuLiveFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _buffer.TryAdd(frame);
    }

    public Task<IReadOnlyList<ReplayInputEvent>> ReadInputEventsAsync(ReplayArtifact artifact, CancellationToken cancellationToken = default)
    {
        // Live source does not produce per-column input events; it is provisional aggregate only.
        // Callers must use GetLiveSnapshot for provisional metrics and later finalize with a file.
        return Task.FromResult<IReadOnlyList<ReplayInputEvent>>(Array.Empty<ReplayInputEvent>());
    }

    public ReplayLiveSnapshot GetLiveSnapshot()
    {
        IReadOnlyList<TosuLiveFrame> frames = _buffer.Snapshot();
        if (frames.Count == 0)
        {
            return new ReplayLiveSnapshot(
                Provenance: _provenance,
                MapProgressMs: null,
                Score: null,
                AggregateUr: null,
                RecentOffsets: [],
                Diagnostics: [new ReplayDiagnostic(ReplayDiagnosticSeverity.Information, "replay.live.empty", "No live frames buffered.")]);
        }

        TosuLiveFrame latest = frames[^1];
        double? aggregateUr = CalculateAggregateUr(frames);
        int[] recentOffsets = frames
            .Where(item => item.HitOffsetMs is not null)
            .TakeLast(20)
            .Select(item => item.HitOffsetMs!.Value)
            .ToArray();

        List<ReplayDiagnostic> diagnostics = [];
        if (aggregateUr is null)
        {
            diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticSeverity.Information, "replay.live.no_offsets", "No hit offsets in provisional buffer; UR unavailable."));
        }

        diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticSeverity.Information, "replay.live.provisional", "Per-column and LN conclusions are suppressed in live mode."));

        return new ReplayLiveSnapshot(
            Provenance: _provenance,
            MapProgressMs: latest.MapTimeMs,
            Score: latest.Score,
            AggregateUr: aggregateUr,
            RecentOffsets: recentOffsets,
            Diagnostics: diagnostics);
    }

    public ReplayJudgeResult FinalizeWithReplayFile(
        OsuManiaBeatmap beatmap,
        IReadOnlyList<ReplayInputEvent> replayInputs,
        ReplayJudgeOptions? options = null)
    {
        // After play, the provisional snapshot is replaced by exact replay-file analysis.
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(replayInputs);
        return ReplayJudge.Judge(beatmap, replayInputs, options);
    }

    private static double? CalculateAggregateUr(IReadOnlyList<TosuLiveFrame> frames)
    {
        double[] offsets = frames.Where(item => item.HitOffsetMs is not null).Select(item => (double)item.HitOffsetMs!.Value).ToArray();
        if (offsets.Length < 2)
        {
            return null;
        }

        double mean = offsets.Average();
        double variance = offsets.Select(value => (value - mean) * (value - mean)).Average();
        return Math.Sqrt(variance) * 10;
    }
}

public sealed record TosuLiveFrame(
    int MapTimeMs,
    int? Score = null,
    int? HitOffsetMs = null,
    string? RawPayloadHash = null)
{
    public string? RawPayloadHash { get; } = RawPayloadHash;
}

public sealed record ReplayLiveSnapshot(
    ReplayProvenance Provenance,
    int? MapProgressMs,
    int? Score,
    double? AggregateUr,
    IReadOnlyList<int> RecentOffsets,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);

internal sealed class BoundedReplayBuffer
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<TosuLiveFrame> _queue = new();
    private int _count;

    public BoundedReplayBuffer(int capacity)
    {
        _capacity = capacity;
    }

    public int Count => Volatile.Read(ref _count);

    public bool TryAdd(TosuLiveFrame frame)
    {
        _queue.Enqueue(frame);
        Interlocked.Increment(ref _count);

        while (Count > _capacity && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }

        // Backpressure: if still over capacity (concurrent race), drop oldest.
        return true;
    }

    public IReadOnlyList<TosuLiveFrame> Snapshot()
    {
        return _queue.ToArray();
    }
}
