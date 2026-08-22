using System;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Validates the analyzer-neutral snapshot emitted by a trusted package bridge.
/// Source-specific extraction remains in the package's editable adapter.js.
/// </summary>
internal sealed class JsonAnalyzerAdapter : IAnalyzerAdapter
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public JsonAnalyzerAdapter(AnalyzerDescriptor descriptor) => Descriptor = descriptor;

    public AnalyzerDescriptor Descriptor
    {
        get;
    }

    public bool TryNormalize(string payload, out AnalysisSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(payload, _jsonOptions);
            return snapshot is not null &&
                   snapshot.SchemaVersion == Descriptor.SnapshotSchemaVersion &&
                   string.Equals(snapshot.SourceId, Descriptor.Id, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The analyzer adapter emitted an invalid analysis snapshot.", exception);
        }
    }
}
