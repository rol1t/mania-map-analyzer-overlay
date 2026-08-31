using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;

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
        // Keep settings written by older builds usable after the built-in
        // preset catalog was consolidated into the Companella family.
        var requestedLayout = NormalizeLayout(requestedPreset);
        var effectivePresetId = requestedLayout == "custom" ? requestedPreset : requestedLayout;
        var preset = _presets.Require(effectivePresetId);
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
        var pauseCoachScript = RequireRuntimeAsset("pause-coach.js");
        var rendererScript = RequireRuntimeAsset("renderer.js");
        var danAssetsScript = BuildDanAssetsScript();
        var adapterScript = analyzer.ReadBridgeScript();

        var setup = BuildSetupScript(
            css, customCss, interactionCss, template, analyzer.HostSelector, analyzer.PresetAnchorSelector,
            layout, overlayMode, scale, presetWidth);
        var observer = BuildRuntimeScript(
            hostScript, pauseCoachScript, rendererScript, danAssetsScript, adapterScript, resizeHandleCss, analyzer.HostSelector, overlayMode,
            PauseCoachOptions.Default);

        var fullscreenSetup = BuildSetupScript(
            css, customCss, interactionCss, template, analyzer.HostSelector, analyzer.PresetAnchorSelector,
            layout, true, scale, presetWidth);
        var fullscreenObserver = BuildRuntimeScript(
            hostScript, pauseCoachScript, rendererScript, danAssetsScript, adapterScript, resizeHandleCss, analyzer.HostSelector, false,
            PauseCoachOptions.Default);

        return new PresentationScripts(setup, observer, fullscreenSetup, fullscreenObserver);
    }

    public AnalyzerAdapterPackage ResolveAnalyzer(string? analyzerId) => _analyzers.Require(analyzerId);

    public static string NormalizeLayout(string? layout)
    {
        var value = (layout ?? OverlayPresetCatalog.DefaultPresetId).Trim().ToLowerInvariant();
        return value switch
        {
            "companella" or "companella-replay" or
            "companella-glass" or "companella-radar" => value,
            "default" or "horizontal" or "companella-focus" or "companella-neon" => "companella",
            "pause-coach-card" or "pause-coach-minimal" or "pause-coach-signal" => "companella-replay",
            _ => "custom"
        };
    }

    private static string GetPresetWidth(string layout) => layout switch
    {
        "companella" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(900, 1d),
        "companella-replay" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(900, 1d),
        "companella-glass" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(920, 1d),
        "companella-radar" => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(1080, 1d),
        _ => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(900, 1d)
    };

    private string RequireRuntimeAsset(string fileName) =>
        _presets.ReadRuntimeAsset(fileName) ?? throw new FileNotFoundException(
            $"Overlay runtime resource '{fileName}' was not found. Rebuild the application package.", fileName);

    private static string BuildDanAssetsScript()
    {
        var root = Path.Combine(AppPaths.ResourceDirectory, "Assets", "overlay", "runtime", "dan-images");
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"DAN image directory '{root}' was not found. Rebuild the application package.");
        }

        var assets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            var mimeType = extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? "image/svg+xml"
                : extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    ? "image/webp"
                    : null;
            if (mimeType is null)
            {
                continue;
            }

            var key = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            assets[key] = $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }

        if (assets.Count == 0)
        {
            throw new InvalidDataException($"DAN image directory '{root}' does not contain supported assets.");
        }

        return "window.__overlayDanAssets=Object.freeze(" + JsonSerializer.Serialize(assets) + ");";
    }

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
            "document.documentElement.classList.toggle('overlay-layout-companella'," + Bool(IsCompanellaFamily(layout)) + ");" +
            "document.documentElement.classList.toggle('overlay-layout-companella-replay'," + Bool(layout == "companella-replay") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-companella-glass'," + Bool(layout == "companella-glass") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-companella-radar'," + Bool(layout == "companella-radar") + ");" +
            "document.documentElement.classList.toggle('overlay-layout-custom'," + Bool(layout == "custom") + ");" +
            "if(!" + Bool(transparent) + "){var fitPreview=function(){var root=document.documentElement,hostScale=parseFloat(root.style.getPropertyValue('--overlay-host-scale'))||1,base=parseFloat(root.style.getPropertyValue('--overlay-preset-width'))||760,available=Math.max(240,(window.innerWidth-36)/hostScale);root.style.setProperty('--overlay-preview-width',Math.min(base,available)+'px');};if(window._overlayPreviewFit)window.removeEventListener('resize',window._overlayPreviewFit);window._overlayPreviewFit=fitPreview;window.addEventListener('resize',fitPreview);fitPreview();}else{if(window._overlayPreviewFit)window.removeEventListener('resize',window._overlayPreviewFit);window._overlayPreviewFit=null;document.documentElement.style.removeProperty('--overlay-preview-width');}" +
            "document.querySelectorAll('[data-overlay-host-root]').forEach(function(node){node.removeAttribute('data-overlay-host-root');});var card=document.querySelector(" + Js(hostSelector) + ");if(card){card.setAttribute('data-overlay-host-root','');card.querySelectorAll('[data-overlay-preset-node],.overlay-pause-coach').forEach(function(node){node.remove();});var markup=" + Js(template) + ";if(markup){var parsed=document.createElement('template');parsed.innerHTML=markup;var anchorSelector=" + Js(presetAnchorSelector ?? string.Empty) + ",anchor=anchorSelector?card.querySelector(anchorSelector):null;Array.from(parsed.content.children).filter(function(node){return node.hasAttribute('data-overlay-preset-node');}).forEach(function(node){card.insertBefore(node,anchor);});}}" +
            "})();";
    }

    private static string BuildRuntimeScript(
        string hostScript,
        string pauseCoachScript,
        string rendererScript,
        string danAssetsScript,
        string adapterScript,
        string resizeHandleCss,
        string hostSelector,
        bool overlayMode,
        PauseCoachOptions pauseCoachOptions)
    {
        ArgumentNullException.ThrowIfNull(pauseCoachOptions);
        var configuration = JsonSerializer.Serialize(new
        {
            overlayMode,
            // Desktop overlay presentation receives the canonical native
            // realtime view-state from the Application runtime. The browser
            // adapter still collects map metadata and gameplay traces, but it
            // must not start a second Pause Coach session on this surface.
            nativeRealtimeAuthority = overlayMode,
            hostSelector,
            resizeHandleCss
        });
        var pauseCoachConfiguration = JsonSerializer.Serialize(new
        {
            recentWindowSeconds = pauseCoachOptions.RecentWindowSeconds,
            baselineWindowSeconds = pauseCoachOptions.BaselineWindowSeconds,
            minimumTimingSamples = pauseCoachOptions.MinimumTimingSamples,
            timingBiasThresholdMs = pauseCoachOptions.TimingBiasThresholdMs,
            timingInstabilityUrThreshold = pauseCoachOptions.TimingInstabilityUrThreshold,
            timingInstabilityMultiplier = pauseCoachOptions.TimingInstabilityMultiplier,
            accuracyDropThreshold = pauseCoachOptions.AccuracyDropThreshold,
            missSpikeMultiplier = pauseCoachOptions.MissSpikeMultiplier,
            minimumMissesForSpike = pauseCoachOptions.MinimumMissesForSpike,
            sectionAccuracyDropThreshold = pauseCoachOptions.SectionAccuracyDropThreshold,
            maxTimelineEvents = pauseCoachOptions.MaxTimelineEvents,
            maxTimingSamples = pauseCoachOptions.MaxTimingSamples,
            maxInsights = pauseCoachOptions.MaxInsights
        });
        var fullscreenViewStateTransport = overlayMode
            ? string.Empty
            : "(function(){var key='__overlayFullscreenViewStatePoll';var previous=window[key];if(previous&&typeof previous.stop==='function')previous.stop();var lastEpoch=String(window.__overlayPresentationEpoch||'');var lastVersion=Number(window.__overlayLatestViewStateVersion||0);var lastErrorAt=0;var stopped=false;async function pull(){if(stopped)return;try{var response=await fetch('/ManiaMapAnalyzerOverlay/view-state.json?t='+Date.now(),{cache:'no-store'});if(!response.ok)return;var state=await response.json();var epoch=String(state&&state.presentationEpoch||'');var version=Number(state&&state.version||0);if(epoch&&epoch!==lastEpoch){lastEpoch=epoch;lastVersion=-1;}if(version>lastVersion){lastVersion=version;window.dispatchEvent(new CustomEvent('overlay:view-state',{detail:state}));}}catch(exception){var now=Date.now();if(now-lastErrorAt>5000){lastErrorAt=now;console.debug('Fullscreen view-state refresh failed',exception);}}}var timer=window.setInterval(pull,250);window[key]={stop:function(){stopped=true;window.clearInterval(timer);}};pull();})();";
        return "window.__overlayHostConfig=" + configuration + ";window.__overlayPauseCoachOptions=" + pauseCoachConfiguration + ";" + Environment.NewLine +
               danAssetsScript + Environment.NewLine +
               hostScript + Environment.NewLine +
               pauseCoachScript + Environment.NewLine +
               rendererScript + Environment.NewLine +
               adapterScript + Environment.NewLine +
               fullscreenViewStateTransport;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool IsCompanellaFamily(string layout) => layout is
        "companella" or "companella-glass" or "companella-radar";
}

public sealed record PresentationScripts(
    string SetupScript,
    string ObserverScript,
    string FullscreenSetupScript,
    string FullscreenObserverScript);
