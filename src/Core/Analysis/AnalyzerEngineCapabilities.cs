using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Describes the features an analyzer engine can provide to the execution
/// layer. The collections are copied to immutable arrays so a descriptor can
/// safely be shared by several consumers.
/// </summary>
public sealed record AnalyzerEngineCapabilities
{
    public AnalyzerEngineCapabilities(
        bool supportsProfiles = false,
        bool supportsMods = false,
        bool supportsRate = false,
        bool supportsCancellation = false,
        IEnumerable<string>? supportedAlgorithms = null,
        IEnumerable<string>? supportedMetricIds = null)
    {
        SupportsProfiles = supportsProfiles;
        SupportsMods = supportsMods;
        SupportsRate = supportsRate;
        SupportsCancellation = supportsCancellation;
        SupportedAlgorithms = NormalizeIds(supportedAlgorithms, StringComparer.Ordinal);
        SupportedMetricIds = NormalizeIds(supportedMetricIds, StringComparer.OrdinalIgnoreCase);
    }

    public bool SupportsProfiles
    {
        get;
    }

    public bool SupportsMods
    {
        get;
    }

    public bool SupportsRate
    {
        get;
    }

    public bool SupportsCancellation
    {
        get;
    }

    public ImmutableArray<string> SupportedAlgorithms
    {
        get;
    }

    public ImmutableArray<string> SupportedMetricIds
    {
        get;
    }

    private static ImmutableArray<string> NormalizeIds(
        IEnumerable<string>? values,
        StringComparer comparer)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(comparer)
            .OrderBy(value => value, comparer)
            .ToImmutableArray();
    }
}
