using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Identifies an analyzer engine and advertises the capabilities available to
/// the planner. This contract intentionally contains no UI or transport data.
/// </summary>
public sealed record AnalyzerEngineDescriptor
{
    public AnalyzerEngineDescriptor(
        string id,
        string name,
        string version,
        AnalyzerEngineCapabilities? capabilities = null,
        IEnumerable<string>? supportedProfiles = null,
        string? upstreamVersion = null,
        int maxConcurrency = 1,
        AnalyzerEngineThreadSafety threadSafety = AnalyzerEngineThreadSafety.Serialized)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("An analyzer engine id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An analyzer engine name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("An analyzer engine version is required.", nameof(version));
        }

        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                maxConcurrency,
                "Analyzer engine concurrency must be at least one.");
        }

        Id = id.Trim();
        Name = name.Trim();
        Version = version.Trim();
        UpstreamVersion = string.IsNullOrWhiteSpace(upstreamVersion)
            ? Version
            : upstreamVersion.Trim();
        MaxConcurrency = maxConcurrency;
        ThreadSafety = threadSafety;
        Capabilities = capabilities ?? new AnalyzerEngineCapabilities();
        SupportedProfiles = (supportedProfiles ?? Array.Empty<string>())
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Select(profile => profile.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    public string Id
    {
        get;
    }

    public string Name
    {
        get;
    }

    public string Version
    {
        get;
    }

    public string UpstreamVersion
    {
        get;
    }

    public int MaxConcurrency
    {
        get;
    }

    public AnalyzerEngineThreadSafety ThreadSafety
    {
        get;
    }

    public AnalyzerEngineCapabilities Capabilities
    {
        get;
    }

    public ImmutableArray<string> SupportedProfiles
    {
        get;
    }
}

public enum AnalyzerEngineThreadSafety
{
    Serialized,
    Concurrent
}
