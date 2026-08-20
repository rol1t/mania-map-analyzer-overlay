using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Avalonia.Models;

/// <summary>
/// Metadata for an overlay template that is backed by external HTML/CSS assets.
/// </summary>
public sealed class OverlayPresetDefinition
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = string.Empty;
    public string? NameRu
    {
        get; set;
    }
    public string Description { get; set; } = string.Empty;
    public string? DescriptionRu
    {
        get; set;
    }
    public string Template { get; set; } = "template.html";
    public string Stylesheet { get; set; } = "style.css";
    public string? RequiredCssMarker
    {
        get; set;
    }
    public string? Script
    {
        get; set;
    }
    public string VisibilityPolicy { get; set; } = "always";
    public bool RequiresScriptPermission
    {
        get; set;
    }
    public bool SupportsFullscreen { get; set; } = true;
    public int MinWidth { get; set; } = 240;
    public int MinHeight { get; set; } = 180;

    [JsonIgnore]
    public string? SourceDirectory
    {
        get; set;
    }
}
