using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Normalized compute configuration. Visual preset provenance is deliberately
/// absent so widgets with identical analyzer settings can share one execution.
/// </summary>
public sealed record AnalysisConfiguration
{
    [JsonConstructor]
    public AnalysisConfiguration(
        string requestedAlgorithm,
        string version,
        ImmutableDictionary<string, JsonElement>? options)
        : this(
            requestedAlgorithm,
            version,
            options as IEnumerable<KeyValuePair<string, JsonElement>>)
    {
    }

    public AnalysisConfiguration(
        string requestedAlgorithm,
        string version,
        IEnumerable<KeyValuePair<string, JsonElement>>? options = null)
    {
        if (string.IsNullOrWhiteSpace(requestedAlgorithm))
        {
            throw new ArgumentException("A requested algorithm is required.", nameof(requestedAlgorithm));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("An analysis configuration version is required.", nameof(version));
        }

        RequestedAlgorithm = requestedAlgorithm.Trim();
        Version = version.Trim();
        Options = JsonElementContract.CloneDictionary(options, nameof(options));
    }

    public string RequestedAlgorithm
    {
        get;
    }

    public string Version
    {
        get;
    }

    public ImmutableDictionary<string, JsonElement> Options
    {
        get;
    }

    internal string GetStableIdentity()
    {
        var builder = new StringBuilder();
        builder.Append(JsonSerializer.Serialize(RequestedAlgorithm));
        builder.Append('\n');
        builder.Append(JsonSerializer.Serialize(Version));
        builder.Append('\n');

        foreach (var option in Options.OrderBy(option => option.Key, StringComparer.Ordinal))
        {
            builder.Append(JsonSerializer.Serialize(option.Key));
            builder.Append('=');
            builder.Append(JsonElementContract.ToCanonicalJson(option.Value));
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
