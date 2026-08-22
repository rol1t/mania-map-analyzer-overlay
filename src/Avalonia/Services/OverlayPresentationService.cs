using System;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Composes a source-independent overlay host from a preset renderer and the
/// currently selected analyzer adapter package.
/// </summary>
public sealed class OverlayPresentationService
{
    private readonly OverlayPresetCatalog _presets;
    private readonly AnalyzerAdapterCatalog _analyzers;

    public OverlayPresentationService()
        : this(new OverlayPresetCatalog(), new AnalyzerAdapterCatalog())
    {
    }

    public OverlayPresentationService(OverlayPresetCatalog presets, AnalyzerAdapterCatalog analyzers)
    {
        this._presets = presets;
        this._analyzers = analyzers;
    }

    public PresentationScripts Build(LauncherSettings settings, bool overlayMode)
    {
        var requestedPreset = string.IsNullOrWhiteSpace(settings.OverlayPresetId) ||
                              (settings.OverlayPresetId == "default" && settings.OverlayLayoutMode != "default")
            ? settings.OverlayLayoutMode
            : settings.OverlayPresetId;
        var preset = _presets.Require(requestedPreset);
        var analyzer = _analyzers.Require(settings.AnalyzerProviderId);
        var layout = NormalizeLayout(preset.Id);
        var scale = Math.Clamp(settings.OverlayScalePercent, 50, 180) / 100d;
        var presetWidth = GetPresetWidth(layout);

        var css = _presets.ReadStylesheet(preset.Id) ?? string.Empty;
        var template = _presets.ReadTemplate(preset.Id) ?? string.Empty;
        var customCss = layout == "custom" ? CustomCssService.Read() : string.Empty;
        var interactionCss = RequireRuntimeAsset("interaction.css");
        var resizeHandleCss = RequireRuntimeAsset("resize-handles.css");
        var hostScript = RequireRuntimeAsset("host.js");
        var rendererScript = RequireRuntimeAsset("renderer.js");
        var adapterScript = analyzer.ReadBridgeScript();

        var setup = BuildSetupScript(
            css, customCss, interactionCss, template, analyzer.HostSelector, analyzer.PresetAnchorSelector,
            layout, overlayMode, scale, presetWidth);
        var observer = BuildRuntimeScript(
            hostScript, rendererScript, adapterScript, resizeHandleCss, analyzer.HostSelector, overlayMode);

        var fullscreenSetup = BuildSetupScript(
            css, customCss, interactionCss, template, analyzer.HostSelector, analyzer.PresetAnchorSelector,
            layout, true, scale, presetWidth);
        var fullscreenObserver = BuildRuntimeScript(
            hostScript, rendererScript, adapterScript, resizeHandleCss, analyzer.HostSelector, false);

        return new PresentationScripts(setup, observer, fullscreenSetup, fullscreenObserver);
    }

    public AnalyzerAdapterPackage ResolveAnalyzer(string? analyzerId) => _analyzers.Require(analyzerId);

    public static string NormalizeLayout(string? layout)
    {
        var value = (layout ?? "default").Trim().ToLowerInvariant();
        return value is "default" or "horizontal" or "companella" or "companella-replay" ? value : "custom";
    }

    private static string GetPresetWidth(string layout) => layout switch
    {
        "horizontal" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(920, 1d),
        "companella" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(760, 1d),
        "companella-replay" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(760, 1d),
        _ => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(475, 1d)
    };

    private string RequireRuntimeAsset(string fileName) =>
        _presets.ReadRuntimeAsset(fileName) ?? throw new FileNotFoundException(
            $"Overlay runtime resource '{fileName}' was not found. Rebuild the application package.", fileName);

    private static string BuildSetupScript(
        string css,
        string customCss,
        string interactionCss,
        string template,
        string hostSelector,
        string? presetAnchorSelector,
        string layout,
        bool transparent,
        double scale,
        string presetWidth)
    {
        string Js(string value) => JsonSerializer.Serialize(value);
        return "(function(){" +
            "var s=document.getElementById('launcher-host-style');if(!s){s=document.createElement('style');s.id='launcher-host-style';document.head.appendChild(s);}s.textContent=" + Js(css) + ";" +
            "var c=document.getElementById('launcher-custom-style');if(!c){c=document.createElement('style');c.id='launcher-custom-style';document.head.appendChild(c);}c.textContent=" + Js(customCss) + ";" +
            "var i=document.getElementById('launcher-interaction-style');if(!i){i=document.createElement('style');i.id='launcher-interaction-style';document.head.appendChild(i);}i.textContent=" + Js(interactionCss) + ";" +
            "document.documentElement.style.setProperty('--overlay-host-scale'," + Js(scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)) + ");" +
            "document.documentElement.style.setProperty('--overlay-preset-width'," + Js(presetWidth) + ");" +
            "document.documentElement.classList.remove('overlay-osu-focused');" +
            "document.documentElement.classList.toggle('launcher-overlay-host',true);" +
            "document.documentElement.classList.toggle('launcher-transparent-overlay'," + Bool(transparent) + ");" +
            "document.documentElement.classList.toggle('overlay-layout-default'," + Bool(layout == "default") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-horizontal'," + Bool(layout == "horizontal") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-companella'," + Bool(layout == "companella") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-companella-replay'," + Bool(layout == "companella-replay") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-custom'," + Bool(layout == "custom") + ");" +
            "if(!" + Bool(transparent) + "){var fitPreview=function(){var root=document.documentElement,hostScale=parseFloat(root.style.getPropertyValue('--overlay-host-scale'))||1,base=parseFloat(root.style.getPropertyValue('--overlay-preset-width'))||760,available=Math.max(240,(window.innerWidth-36)/hostScale);root.style.setProperty('--overlay-preview-width',Math.min(base,available)+'px');};if(window._overlayPreviewFit)window.removeEventListener('resize',window._overlayPreviewFit);window._overlayPreviewFit=fitPreview;window.addEventListener('resize',fitPreview);fitPreview();}else{if(window._overlayPreviewFit)window.removeEventListener('resize',window._overlayPreviewFit);window._overlayPreviewFit=null;document.documentElement.style.removeProperty('--overlay-preview-width');}" +
            "document.querySelectorAll('[data-overlay-host-root]').forEach(function(node){node.removeAttribute('data-overlay-host-root');});var card=document.querySelector(" + Js(hostSelector) + ");if(card){card.setAttribute('data-overlay-host-root','');card.querySelectorAll('[data-overlay-preset-node]').forEach(function(node){node.remove();});var markup=" + Js(template) + ";if(markup){var parsed=document.createElement('template');parsed.innerHTML=markup;var anchorSelector=" + Js(presetAnchorSelector ?? string.Empty) + ",anchor=anchorSelector?card.querySelector(anchorSelector):null;Array.from(parsed.content.children).filter(function(node){return node.hasAttribute('data-overlay-preset-node');}).forEach(function(node){card.insertBefore(node,anchor);});}}" +
            "})();";
    }

    private static string BuildRuntimeScript(
        string hostScript,
        string rendererScript,
        string adapterScript,
        string resizeHandleCss,
        string hostSelector,
        bool overlayMode)
    {
        var configuration = JsonSerializer.Serialize(new
        {
            overlayMode,
            hostSelector,
            resizeHandleCss
        });
        return "window._overlayHostConfig=" + configuration + ";" + Environment.NewLine +
               hostScript + Environment.NewLine +
               rendererScript + Environment.NewLine +
               adapterScript;
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

public sealed record PresentationScripts(
    string SetupScript,
    string ObserverScript,
    string FullscreenSetupScript,
    string FullscreenObserverScript);
