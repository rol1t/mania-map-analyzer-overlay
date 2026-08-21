using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        return new EffectiveWidgetSpec(
            "headless-overlay",
            ImmutableArray.Create(source),
            ImmutableArray.Create(binding));
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
