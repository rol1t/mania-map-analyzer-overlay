using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Analyzer-neutral metric identified by a semantic id rather than a widget
/// selector or an analyzer-specific field name. The value is a cloned JSON
/// element so primitive, object, and array metrics remain typed and immutable.
/// </summary>
public sealed record SemanticMetric
{
    [JsonConstructor]
    public SemanticMetric(
        string id,
        JsonElement value,
        string? unit = null,
        ImmutableDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A metric id is required.", nameof(id));
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A defined JSON metric value is required.", nameof(value));
        }

        Id = id.Trim();
        Value = value.Clone();
        Unit = unit?.Trim() ?? string.Empty;
        Metadata = (metadata ?? ImmutableDictionary<string, string>.Empty)
            .ToImmutableDictionary(
                item => item.Key.Trim(),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public string Id
    {
        get;
    }

    public JsonElement Value
    {
        get;
    }

    public string Unit
    {
        get;
    }

    public ImmutableDictionary<string, string> Metadata
    {
        get;
    }

    public static SemanticMetric FromValue<T>(
        string id,
        T value,
        string? unit = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        var jsonValue = JsonSerializer.SerializeToElement(value, serializerOptions);
        var immutableMetadata = (metadata ?? Array.Empty<KeyValuePair<string, string>>())
            .ToImmutableDictionary(
                item => item.Key.Trim(),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        return new SemanticMetric(id, jsonValue, unit, immutableMetadata);
    }
}
