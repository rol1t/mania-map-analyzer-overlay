using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Describes a DOM-free analyzer engine package. The manifest is metadata only;
/// loading it never imports or starts either of the package scripts.
/// </summary>
public sealed class AnalyzerEngineManifest
{
    [JsonPropertyName("manifestSchemaVersion")]
    public int ManifestSchemaVersion
    {
        get; set;
    }

    [JsonPropertyName("kind")]
    public string Kind
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("id")]
    public string Id
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name
    {
        get; set;
    }

    [JsonPropertyName("version")]
    public string Version
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion
    {
        get; set;
    }

    [JsonPropertyName("runtime")]
    public string Runtime
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("worker")]
    public string Worker
    {
        get; set;
    } = string.Empty;

    [JsonPropertyName("defaultPipelinePath")]
    public string? DefaultPipelinePath
    {
        get; set;
    }

    [JsonPropertyName("defaultCompanellaPath")]
    public string? DefaultCompanellaPath
    {
        get; set;
    }

    [JsonPropertyName("defaultMixedEstimatorPath")]
    public string? DefaultMixedEstimatorPath
    {
        get; set;
    }

    [JsonPropertyName("pipelineExport")]
    public string? PipelineExport
    {
        get; set;
    }

    [JsonPropertyName("companellaExport")]
    public string? CompanellaExport
    {
        get; set;
    }

    [JsonPropertyName("mixedCompanellaExport")]
    public string? MixedCompanellaExport
    {
        get; set;
    }

    [JsonPropertyName("upstream")]
    public AnalyzerEngineUpstreamManifest? Upstream
    {
        get; set;
    }

    [JsonPropertyName("capabilities")]
    public AnalyzerEngineCapabilitiesManifest? Capabilities
    {
        get; set;
    }

    [JsonPropertyName("notes")]
    public List<string>? Notes
    {
        get; set;
    }
}

/// <summary>Upstream project information carried by an analyzer engine manifest.</summary>
public sealed class AnalyzerEngineUpstreamManifest
{
    [JsonPropertyName("name")]
    public string? Name
    {
        get; set;
    }

    [JsonPropertyName("repository")]
    public string? Repository
    {
        get; set;
    }

    [JsonPropertyName("license")]
    public string? License
    {
        get; set;
    }

    [JsonPropertyName("integration")]
    public string? Integration
    {
        get; set;
    }

    /// <summary>
    /// An optional single exact version selector. New manifests should prefer
    /// <see cref="SupportedVersions"/> so compatibility remains explicit.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version
    {
        get; set;
    }

    /// <summary>
    /// Exact upstream versions supported by the package. Entries are opaque
    /// selectors for the future host; the catalog only requires non-empty
    /// values and does not guess range semantics.
    /// </summary>
    [JsonPropertyName("supportedVersions")]
    public List<string>? SupportedVersions
    {
        get; set;
    }
}

/// <summary>Capabilities advertised by an analyzer engine package.</summary>
public sealed class AnalyzerEngineCapabilitiesManifest
{
    [JsonPropertyName("algorithms")]
    public List<string>? Algorithms
    {
        get; set;
    }

    [JsonPropertyName("optionalAlgorithms")]
    public Dictionary<string, AnalyzerEngineOptionalAlgorithmManifest>? OptionalAlgorithms
    {
        get; set;
    }

    [JsonPropertyName("semanticMetricIds")]
    public List<string>? SemanticMetricIds
    {
        get; set;
    }
}

/// <summary>Optional capability requiring runtime probing or named exports.</summary>
public sealed class AnalyzerEngineOptionalAlgorithmManifest
{
    [JsonPropertyName("requiresRuntimeProbe")]
    public bool RequiresRuntimeProbe
    {
        get; set;
    }

    [JsonPropertyName("requiresExports")]
    public List<string>? RequiresExports
    {
        get; set;
    }
}
