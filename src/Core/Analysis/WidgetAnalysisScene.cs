using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Immutable definition of one analysis scene. Widget order is significant and
/// is preserved in the composed scene snapshot.
/// </summary>
public sealed record WidgetAnalysisSceneSpec
{
    public WidgetAnalysisSceneSpec(
        string sceneId,
        IEnumerable<WidgetAnalysisSpec> widgets)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            throw new ArgumentException("An analysis scene id is required.", nameof(sceneId));
        }

        ArgumentNullException.ThrowIfNull(widgets);
        var normalizedWidgets = widgets.ToImmutableArray();
        if (normalizedWidgets.IsEmpty)
        {
            throw new ArgumentException("At least one widget is required.", nameof(widgets));
        }

        if (normalizedWidgets.Any(widget => widget is null))
        {
            throw new ArgumentException("A scene cannot contain a null widget.", nameof(widgets));
        }

        var duplicate = normalizedWidgets
            .GroupBy(widget => widget.WidgetId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Widget id '{duplicate.Key}' is configured more than once in scene '{sceneId}'.",
                nameof(widgets));
        }

        SceneId = sceneId.Trim();
        Widgets = normalizedWidgets;
    }

    public string SceneId
    {
        get;
    }

    public ImmutableArray<WidgetAnalysisSpec> Widgets
    {
        get;
    }
}

/// <summary>
/// Atomic result of one scene generation. <see cref="OrderedSnapshots"/>
/// preserves scene definition order, while <see cref="SnapshotsByWidgetId"/>
/// provides stable id lookup.
/// </summary>
public sealed record WidgetAnalysisSceneSnapshot
{
    public WidgetAnalysisSceneSnapshot(
        string sceneId,
        long generation,
        IEnumerable<ComposedWidgetSnapshot> orderedSnapshots)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            throw new ArgumentException("An analysis scene id is required.", nameof(sceneId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "An analysis scene generation must be positive.");
        }

        ArgumentNullException.ThrowIfNull(orderedSnapshots);
        var normalizedSnapshots = orderedSnapshots.ToImmutableArray();
        if (normalizedSnapshots.IsEmpty)
        {
            throw new ArgumentException("At least one widget snapshot is required.", nameof(orderedSnapshots));
        }

        if (normalizedSnapshots.Any(snapshot => snapshot is null))
        {
            throw new ArgumentException(
                "An analysis scene cannot contain a null widget snapshot.",
                nameof(orderedSnapshots));
        }

        var duplicate = normalizedSnapshots
            .GroupBy(snapshot => snapshot.WidgetId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Widget snapshot '{duplicate.Key}' was supplied more than once.",
                nameof(orderedSnapshots));
        }

        SceneId = sceneId.Trim();
        Generation = generation;
        OrderedSnapshots = normalizedSnapshots;
        SnapshotsByWidgetId = normalizedSnapshots.ToImmutableDictionary(
            snapshot => snapshot.WidgetId,
            StringComparer.Ordinal);
    }

    public string SceneId
    {
        get;
    }

    public long Generation
    {
        get;
    }

    public ImmutableArray<ComposedWidgetSnapshot> OrderedSnapshots
    {
        get;
    }

    public ImmutableDictionary<string, ComposedWidgetSnapshot> SnapshotsByWidgetId
    {
        get;
    }
}
