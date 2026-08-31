using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Models;

/// <summary>
/// Persisted effective analysis configuration. Visual preset/profile selection
/// (CSS, layout) is intentionally not part of this file so a widget can reuse
/// the same analysis result with different presets.
/// </summary>
public sealed record EffectiveAnalysisConfiguration
{
    public const int CurrentSchemaVersion = 1;

    private static readonly ImmutableArray<string> _defaultSkillMetricIds =
    [
        "skills.overall",
        "skills.stream",
        "skills.jumpstream",
        "skills.handstream",
        "skills.stamina",
        "skills.jackspeed",
        "skills.chordjack",
        "skills.technical"
    ];

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string DefaultEngineId { get; init; } = "mania-map-analyser-headless";

    public string DefaultAlgorithm { get; init; } = "Mixed";

    public string ConfigurationVersion { get; init; } = "1";

    public ImmutableDictionary<string, JsonElement> DefaultOptions
    {
        get; init;
    } =
        ImmutableDictionary<string, JsonElement>.Empty;

    public ImmutableArray<EffectiveWidgetSpec> Widgets
    {
        get; init;
    } =
        ImmutableArray<EffectiveWidgetSpec>.Empty;

    public EffectiveAnalysisConfiguration Normalize()
    {
        var engineId = string.IsNullOrWhiteSpace(DefaultEngineId)
            ? "mania-map-analyser-headless"
            : DefaultEngineId.Trim();
        var algorithm = string.IsNullOrWhiteSpace(DefaultAlgorithm)
            ? "Mixed"
            : DefaultAlgorithm.Trim();
        var version = string.IsNullOrWhiteSpace(ConfigurationVersion)
            ? "1"
            : ConfigurationVersion.Trim();
        var options = DefaultOptions.IsEmpty ? ImmutableDictionary<string, JsonElement>.Empty : DefaultOptions;
        var widgets = Widgets.IsDefault ? ImmutableArray<EffectiveWidgetSpec>.Empty : Widgets;

        if (widgets.IsEmpty)
        {
            widgets = ImmutableArray.Create(CreateDefaultWidget(engineId, algorithm, version, options));
        }

        widgets = widgets
            .Select(MigrateLegacyDefaultWidget)
            .ToImmutableArray();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            DefaultEngineId = engineId,
            DefaultAlgorithm = algorithm,
            ConfigurationVersion = version,
            DefaultOptions = options,
            Widgets = widgets,
        };
    }

    private static EffectiveWidgetSpec CreateDefaultWidget(
        string engineId,
        string algorithm,
        string version,
        ImmutableDictionary<string, JsonElement> options)
    {
        var source = new EffectiveAnalysisSource(
            "headless-primary",
            engineId,
            algorithm,
            version,
            options);
        var binding = new EffectiveWidgetBinding(
            "difficulty.star",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "difficulty.star")));
        var difficultyTimelineBinding = new EffectiveWidgetBinding(
            "difficulty.timeline",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "difficulty.timeline")),
            allowsNull: true);
        var riceDifficultyTimelineBinding = new EffectiveWidgetBinding(
            "difficulty.rice.timeline",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "difficulty.rice.timeline")),
            allowsNull: true);
        var lnDifficultyTimelineBinding = new EffectiveWidgetBinding(
            "difficulty.ln.timeline",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "difficulty.ln.timeline")),
            allowsNull: true);
        var difficultyLabelBinding = new EffectiveWidgetBinding(
            "difficulty.label",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "difficulty.label")));
        var rcLabelBinding = new EffectiveWidgetBinding(
            "dan.rc.label",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "dan.rc.label")));
        var rcNumericBinding = new EffectiveWidgetBinding(
            "dan.rc.numeric",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "dan.rc.numeric")));
        var lnPercentBinding = new EffectiveWidgetBinding(
            "difficulty.lnPercent",
            [
                new SourceMetricCandidate("headless-primary", "difficulty.lnPercent"),
                new SourceMetricCandidate("headless-primary", "pattern.lnPercent")
            ]);
        var lnLabelBinding = new EffectiveWidgetBinding(
            "dan.ln.label",
            ImmutableArray.Create(new SourceMetricCandidate("headless-primary", "dan.ln.label")));
        var bindings = ImmutableArray.CreateBuilder<EffectiveWidgetBinding>();
        bindings.Add(binding);
        bindings.Add(difficultyTimelineBinding);
        bindings.Add(riceDifficultyTimelineBinding);
        bindings.Add(lnDifficultyTimelineBinding);
        bindings.Add(difficultyLabelBinding);
        bindings.Add(rcLabelBinding);
        bindings.Add(rcNumericBinding);
        bindings.Add(lnPercentBinding);
        bindings.Add(lnLabelBinding);
        foreach (string metricId in _defaultSkillMetricIds)
        {
            bindings.Add(new EffectiveWidgetBinding(
                metricId,
                ImmutableArray.Create(new SourceMetricCandidate("headless-primary", metricId))));
        }
        return new EffectiveWidgetSpec(
            "headless-overlay",
            ImmutableArray.Create(source),
            bindings);
    }

    private static EffectiveWidgetSpec MigrateLegacyDefaultWidget(EffectiveWidgetSpec widget)
    {
        // Configurations written before all normalized DAN/LN metrics were
        // exposed contain only the generated default bindings. Extend only
        // that known generated shape; leave user-authored mappings untouched.
        if (!string.Equals(widget.WidgetId, "headless-overlay", StringComparison.OrdinalIgnoreCase)
            || widget.Sources.Length != 1
            || !string.Equals(widget.Sources[0].SourceId, "headless-primary", StringComparison.OrdinalIgnoreCase)
            || widget.Bindings.Any(binding => !IsGeneratedDefaultBinding(binding, widget.Sources[0].SourceId)))
        {
            return widget;
        }

        var sourceId = widget.Sources[0].SourceId;
        var bindings = widget.Bindings.ToBuilder();
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "difficulty.timeline",
                [new SourceMetricCandidate(sourceId, "difficulty.timeline")],
                allowsNull: true));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "difficulty.rice.timeline",
                [new SourceMetricCandidate(sourceId, "difficulty.rice.timeline")],
                allowsNull: true));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "difficulty.ln.timeline",
                [new SourceMetricCandidate(sourceId, "difficulty.ln.timeline")],
                allowsNull: true));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "dan.rc.label",
                [new SourceMetricCandidate(sourceId, "dan.rc.label")]));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "dan.rc.numeric",
                [new SourceMetricCandidate(sourceId, "dan.rc.numeric")]));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "difficulty.label",
                [new SourceMetricCandidate(sourceId, "difficulty.label")]));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "difficulty.lnPercent",
                [
                    new SourceMetricCandidate(sourceId, "difficulty.lnPercent"),
                    new SourceMetricCandidate(sourceId, "pattern.lnPercent")
                ]));
        AppendGeneratedBindingIfMissing(
            bindings,
            new EffectiveWidgetBinding(
                "dan.ln.label",
                [new SourceMetricCandidate(sourceId, "dan.ln.label")]));
        foreach (string metricId in _defaultSkillMetricIds)
        {
            AppendGeneratedBindingIfMissing(
                bindings,
                new EffectiveWidgetBinding(
                    metricId,
                    [new SourceMetricCandidate(sourceId, metricId)]));
        }
        return new EffectiveWidgetSpec(widget.WidgetId, widget.Sources, bindings);
    }

    private static bool IsGeneratedDefaultBinding(EffectiveWidgetBinding binding, string sourceId)
    {
        if (binding.Candidates.Any(candidate =>
                !string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (binding.AllowsNull
            && binding.TargetMetricId is not ("difficulty.timeline"
                or "difficulty.rice.timeline"
                or "difficulty.ln.timeline"))
        {
            return false;
        }

        return binding.TargetMetricId switch
        {
            "difficulty.star" or "difficulty.timeline" or "difficulty.rice.timeline" or "difficulty.ln.timeline" or "difficulty.label" or "dan.rc.label" or "dan.rc.numeric" or "dan.ln.label"
                => binding.Candidates.Length == 1
                    && string.Equals(binding.Candidates[0].MetricId, binding.TargetMetricId, StringComparison.OrdinalIgnoreCase),
            "difficulty.lnPercent" => binding.Candidates.All(candidate =>
                string.Equals(candidate.MetricId, "difficulty.lnPercent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.MetricId, "pattern.lnPercent", StringComparison.OrdinalIgnoreCase)),
            _ when binding.TargetMetricId.StartsWith("skills.", StringComparison.OrdinalIgnoreCase)
                => _defaultSkillMetricIds.Contains(binding.TargetMetricId, StringComparer.OrdinalIgnoreCase)
                    && binding.Candidates.Length == 1
                    && string.Equals(binding.Candidates[0].MetricId, binding.TargetMetricId, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static void AppendGeneratedBindingIfMissing(
        ImmutableArray<EffectiveWidgetBinding>.Builder bindings,
        EffectiveWidgetBinding binding)
    {
        if (!bindings.Any(existing => string.Equals(
                existing.TargetMetricId,
                binding.TargetMetricId,
                StringComparison.OrdinalIgnoreCase)))
        {
            bindings.Add(binding);
        }
    }
}

public sealed record EffectiveAnalysisSource
{
    public EffectiveAnalysisSource(
        string sourceId,
        string engineId,
        string requestedAlgorithm,
        string configurationVersion,
        ImmutableDictionary<string, JsonElement>? options = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(engineId))
        {
            throw new ArgumentException("An engine id is required.", nameof(engineId));
        }

        if (string.IsNullOrWhiteSpace(requestedAlgorithm))
        {
            throw new ArgumentException("A requested algorithm is required.", nameof(requestedAlgorithm));
        }

        if (string.IsNullOrWhiteSpace(configurationVersion))
        {
            throw new ArgumentException("A configuration version is required.", nameof(configurationVersion));
        }

        SourceId = sourceId.Trim();
        EngineId = engineId.Trim();
        RequestedAlgorithm = requestedAlgorithm.Trim();
        ConfigurationVersion = configurationVersion.Trim();
        Options = options is null
            ? ImmutableDictionary<string, JsonElement>.Empty
            : options;
    }

    [JsonConstructor]
    public EffectiveAnalysisSource(
        string sourceId,
        string engineId,
        string requestedAlgorithm,
        string configurationVersion,
        Dictionary<string, JsonElement>? options)
        : this(
            sourceId,
            engineId,
            requestedAlgorithm,
            configurationVersion,
            CloneOptions(options))
    {
    }

    private static ImmutableDictionary<string, JsonElement> CloneOptions(Dictionary<string, JsonElement>? options)
    {
        if (options is null || options.Count == 0)
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in options)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("JSON value keys cannot be empty.", nameof(options));
            }

            if (entry.Value.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException("Only defined JSON values can be used in analysis contracts.", nameof(options));
            }

            builder.Add(entry.Key.Trim(), entry.Value.Clone());
        }

        return builder.ToImmutable();
    }

    public string SourceId
    {
        get;
    }

    public string EngineId
    {
        get;
    }

    public string RequestedAlgorithm
    {
        get;
    }

    public string ConfigurationVersion
    {
        get;
    }

    public ImmutableDictionary<string, JsonElement> Options
    {
        get;
    }
}

public sealed record EffectiveWidgetSpec
{
    public EffectiveWidgetSpec(
        string widgetId,
        IEnumerable<EffectiveAnalysisSource> sources,
        IEnumerable<EffectiveWidgetBinding> bindings)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new ArgumentException("A widget id is required.", nameof(widgetId));
        }

        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(bindings);
        var normalizedSources = sources.ToImmutableArray();
        var normalizedBindings = bindings.ToImmutableArray();
        if (normalizedSources.IsEmpty)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        if (normalizedBindings.IsEmpty)
        {
            throw new ArgumentException("At least one binding is required.", nameof(bindings));
        }

        WidgetId = widgetId.Trim();
        Sources = normalizedSources;
        Bindings = normalizedBindings;
    }

    public string WidgetId
    {
        get;
    }

    public ImmutableArray<EffectiveAnalysisSource> Sources
    {
        get;
    }

    public ImmutableArray<EffectiveWidgetBinding> Bindings
    {
        get;
    }
}

public sealed record EffectiveWidgetBinding
{
    public EffectiveWidgetBinding(
        string targetMetricId,
        IEnumerable<SourceMetricCandidate> candidates,
        bool allowsNull = false)
    {
        if (string.IsNullOrWhiteSpace(targetMetricId))
        {
            throw new ArgumentException("A target metric id is required.", nameof(targetMetricId));
        }

        ArgumentNullException.ThrowIfNull(candidates);
        var normalized = candidates.ToImmutableArray();
        if (normalized.IsEmpty)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        TargetMetricId = targetMetricId.Trim();
        Candidates = normalized;
        AllowsNull = allowsNull;
    }

    public string TargetMetricId
    {
        get;
    }

    public ImmutableArray<SourceMetricCandidate> Candidates
    {
        get;
    }

    public bool AllowsNull
    {
        get;
    }
}
