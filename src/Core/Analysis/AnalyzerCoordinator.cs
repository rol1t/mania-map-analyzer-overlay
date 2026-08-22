namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Owns the active analyzer and rejects stale snapshots emitted by an adapter
/// after another adapter has been selected.
/// </summary>
public sealed class AnalyzerCoordinator
{
    private readonly IReadOnlyDictionary<string, IAnalyzerAdapter> _adapters;

    public AnalyzerCoordinator(IEnumerable<IAnalyzerAdapter> adapters, string initialAdapterId)
    {
        _adapters = adapters.ToDictionary(x => x.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        ActiveAdapter = Resolve(initialAdapterId);
    }

    public IAnalyzerAdapter ActiveAdapter
    {
        get; private set;
    }
    public AnalysisSnapshot? CurrentSnapshot
    {
        get; private set;
    }
    public event Action<AnalysisSnapshot>? SnapshotChanged;

    public IReadOnlyCollection<AnalyzerDescriptor> AvailableAdapters =>
        _adapters.Values.Select(x => x.Descriptor).ToArray();

    public IAnalyzerAdapter Switch(string adapterId)
    {
        var next = Resolve(adapterId);
        if (ReferenceEquals(next, ActiveAdapter))
        {
            return ActiveAdapter;
        }

        ActiveAdapter = next;
        CurrentSnapshot = null;
        return next;
    }

    public bool TryAccept(string adapterId, string payload, out AnalysisSnapshot? snapshot)
    {
        snapshot = null;
        if (!string.Equals(adapterId, ActiveAdapter.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ActiveAdapter.TryNormalize(payload, out snapshot) || snapshot is null)
        {
            return false;
        }

        if (snapshot.SchemaVersion != ActiveAdapter.Descriptor.SnapshotSchemaVersion)
        {
            return false;
        }

        if (!string.Equals(snapshot.SourceId, ActiveAdapter.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    private IAnalyzerAdapter Resolve(string? adapterId)
    {
        var normalized = string.IsNullOrWhiteSpace(adapterId) ? "mania-map-analyser" : adapterId.Trim();
        return _adapters.TryGetValue(normalized, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"Analyzer adapter '{normalized}' is not registered.");
    }
}
