using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

internal static class JsonElementContract
{
    public static ImmutableDictionary<string, JsonElement> CloneDictionary(
        IEnumerable<KeyValuePair<string, JsonElement>>? values,
        string parameterName)
    {
        var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in values ?? Array.Empty<KeyValuePair<string, JsonElement>>())
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                throw new ArgumentException("JSON value keys cannot be empty.", parameterName);
            }

            EnsureDefined(item.Value, parameterName);
            normalized[item.Key.Trim()] = item.Value.Clone();
        }

        return normalized.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public static string ToCanonicalJson(JsonElement value)
    {
        EnsureDefined(value, nameof(value));
        var builder = new StringBuilder();
        AppendCanonicalJson(builder, value);
        return builder.ToString();
    }

    private static void AppendCanonicalJson(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var isFirstProperty = true;
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!isFirstProperty)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    AppendCanonicalJson(builder, property.Value);
                    isFirstProperty = false;
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var isFirstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!isFirstItem)
                    {
                        builder.Append(',');
                    }

                    AppendCanonicalJson(builder, item);
                    isFirstItem = false;
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            case JsonValueKind.Number:
                builder.Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new ArgumentException("Only JSON-safe values can be used in analysis contracts.", nameof(value));
        }
    }

    private static void EnsureDefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Only defined JSON values can be used in analysis contracts.", parameterName);
        }
    }
}
