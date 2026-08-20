using System;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public sealed class OverlayPresentationService
{
    public PresentationScripts Build(LauncherSettings settings, bool overlayMode)
    {
        var layout = NormalizeLayout(settings.OverlayLayoutMode);
        var scale = Math.Clamp(settings.OverlayScalePercent, 50, 180) / 100d;
        var defaultWidth = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(475, scale);
        var horizontalWidth = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(920, scale);
        var companellaWidth = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(760, scale);

        var css =
            "html,body{width:100%!important;height:100%!important;min-height:0!important;background:transparent!important;overflow:hidden!important;}" +
            "body{padding:0!important;margin:0!important;}" +
            ".dashboard{min-height:0!important;margin:0!important;gap:0!important;align-content:start!important;}" +
            ".card.main-card{margin:0!important;box-shadow:none!important;}" +
            ManiaMapAnalyzerOverlay.OverlayStyleBuilder.BuildBaseScaleCss(scale);

        var customCss = "";
        if (layout == "horizontal")
        {
            css +=
                "html.mma-layout-horizontal{--mma-host-width:" + horizontalWidth + ";}" +
                "html.mma-layout-horizontal .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                "html.mma-layout-horizontal .main-card{display:grid!important;grid-template-columns:34% minmax(0,66%)!important;grid-template-rows:auto auto!important;grid-auto-rows:auto!important;column-gap:" + Px(20, scale) + "!important;row-gap:" + Px(8, scale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:" + Px(318, scale) + "!important;max-height:none!important;padding:" + Px(14, scale) + " " + Px(16, scale) + " " + Px(34, scale) + "!important;overflow:hidden!important;}" +
                "html.mma-layout-horizontal .main-card.bars-pattern,html.mma-layout-horizontal .main-card.bars-etterna,html.mma-layout-horizontal .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-horizontal .main-card.bars-graph,html.mma-layout-horizontal .main-card.bars-none,html.mma-layout-horizontal .main-card.bars-full{height:auto!important;min-height:" + Px(318, scale) + "!important;max-height:none!important;}" +
                "html.mma-layout-horizontal .status-row{grid-column:1/-1!important;grid-row:1!important;margin:0 0 " + Px(4, scale) + "!important;}" +
                "html.mma-layout-horizontal .star-block{grid-column:1!important;grid-row:2!important;align-self:start!important;display:flex!important;flex-direction:column!important;align-items:stretch!important;justify-content:flex-start!important;gap:" + Px(14, scale) + "!important;min-width:0!important;}" +
                "html.mma-layout-horizontal .star-left{width:100%!important;}" +
                "html.mma-layout-horizontal .star-right-group{width:100%!important;max-width:100%!important;flex:0 0 auto!important;align-items:flex-start!important;justify-content:flex-start!important;}" +
                "html.mma-layout-horizontal .star-right{text-align:left!important;justify-items:start!important;}" +
                "html.mma-layout-horizontal .mma-host-details{grid-column:2!important;grid-row:2!important;display:grid!important;grid-auto-rows:auto!important;gap:" + Px(8, scale) + "!important;align-content:start!important;min-width:0!important;overflow:visible!important;}" +
                "html.mma-layout-horizontal .mma-host-details>[hidden]{display:none!important;}" +
                "html.mma-layout-horizontal .cluster-bars,html.mma-layout-horizontal .ett-skill-bars{height:auto!important;min-height:0!important;max-height:none!important;overflow:visible!important;padding-bottom:" + Px(24, scale) + "!important;margin-bottom:0!important;}" +
                "html.mma-layout-horizontal .body-graph-wrap{width:100%!important;margin:0 auto " + Px(24, scale) + "!important;}" +
                "html.mma-layout-horizontal .main-card.bars-none{grid-template-columns:1fr!important;}" +
                "html.mma-layout-horizontal .main-card.bars-none .star-block{grid-column:1/-1!important;display:flex!important;flex-direction:row!important;align-items:flex-end!important;justify-content:space-between!important;}" +
                "html.mma-layout-horizontal .main-card.bars-none .star-right-group{width:56%!important;max-width:56%!important;}" +
                "html.mma-layout-horizontal .main-card.bars-none .mma-host-details{display:none!important;}";
        }
        else if (layout == "companella")
        {
            css += ManiaMapAnalyzerOverlay.OverlayStyleBuilder.BuildCompanellaCss(scale, companellaWidth);
        }
        else
        {
            css +=
                "html.mma-layout-default,html.mma-layout-custom{--mma-host-width:" + defaultWidth + ";}" +
                "html.mma-layout-default .dashboard,html.mma-layout-custom .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                "html.mma-layout-default .main-card,html.mma-layout-custom .main-card{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                FixedHeight(".main-card", 540, scale) + FixedHeight(".main-card.bars-pattern", 575, scale) +
                FixedHeight(".main-card.bars-graph", 396, scale) + FixedHeight(".main-card.bars-none", 248, scale) +
                FixedHeight(".main-card.bars-etterna", 540, scale) + FixedHeight(".main-card.bars-etterna.bars-etterna-compact", 500, scale) +
                "html.mma-layout-default .main-card.bars-full,html.mma-layout-custom .main-card.bars-full{height:auto!important;min-height:" + Px(540, scale) + "!important;max-height:none!important;}";
            if (layout == "custom") customCss = CustomCssService.Read();
        }

        var typography = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.BuildReadableTypographyCss(scale);
        var fullscreenCss = css + typography;
        if (!overlayMode)
        {
            css +=
                "html,body{width:100%!important;height:100%!important;min-height:100%!important;background:#0e1016!important;overflow:auto!important;}" +
                "body{padding:18px!important;margin:0!important;}" +
                ".dashboard{min-height:0!important;margin:0 auto!important;gap:0!important;align-content:start!important;}" +
                ".card.main-card{margin:0 auto!important;box-shadow:0 16px 38px rgba(0,0,0,.30)!important;}" +
                "html.mma-layout-default,html.mma-layout-custom{--mma-host-width:min(" + defaultWidth + ",calc(100vw - 36px))!important;}" +
                "html.mma-layout-horizontal{--mma-host-width:min(" + horizontalWidth + ",calc(100vw - 36px))!important;}" +
                "html.mma-layout-companella{--mma-host-width:min(" + companellaWidth + ",calc(100vw - 36px))!important;}";
        }
        css += typography;

        var interaction = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.BuildInteractionCss();
        var setup = BuildSetupScript(css, customCss, interaction, layout, overlayMode);
        var fullscreenSetup = BuildSetupScript(fullscreenCss, customCss, interaction, layout, true);
        var observer = BuildObserverScript(overlayMode) + BuildCompanellaChartEnhancementV2();
        var fullscreenObserver = BuildObserverScript(false) + BuildCompanellaChartEnhancementV2();
        return new PresentationScripts(setup, observer, fullscreenSetup, fullscreenObserver);
    }

    public static string NormalizeLayout(string? layout)
    {
        var value = (layout ?? "default").Trim().ToLowerInvariant();
        return value is "default" or "horizontal" or "companella" or "custom" ? value : "default";
    }

    private static string BuildSetupScript(string css, string customCss, string interactionCss, string layout, bool transparent)
    {
        string Js(string value) => JsonSerializer.Serialize(value);
        return "(function(){" +
            "var s=document.getElementById('launcher-host-style');if(!s){s=document.createElement('style');s.id='launcher-host-style';document.head.appendChild(s);}s.textContent=" + Js(css) + ";" +
            "var c=document.getElementById('launcher-custom-style');if(!c){c=document.createElement('style');c.id='launcher-custom-style';document.head.appendChild(c);}c.textContent=" + Js(customCss) + ";" +
            "var i=document.getElementById('launcher-interaction-style');if(!i){i=document.createElement('style');i.id='launcher-interaction-style';document.head.appendChild(i);}i.textContent=" + Js(interactionCss) + ";" +
            "document.documentElement.classList.remove('mma-osu-focused');" +
            "document.documentElement.classList.toggle('launcher-overlay-host',true);" +
            "document.documentElement.classList.toggle('launcher-transparent-overlay'," + Bool(transparent) + ");" +
            "document.documentElement.classList.toggle('mma-layout-default'," + Bool(layout == "default") + ");" +
            "document.documentElement.classList.toggle('mma-layout-horizontal'," + Bool(layout == "horizontal") + ");" +
            "document.documentElement.classList.toggle('mma-layout-companella'," + Bool(layout == "companella") + ");" +
            "document.documentElement.classList.toggle('mma-layout-custom'," + Bool(layout == "custom") + ");" +
            "var card=document.querySelector('.main-card');if(card){card.setAttribute('unselectable','on');card.ondragstart=function(){return false;};card.onselectstart=function(){return false;};}" +
            "var details=document.getElementById('mma-host-details');if(card&&" + Bool(layout is "horizontal" or "companella") + "){if(!details){details=document.createElement('div');details.id='mma-host-details';details.className='mma-host-details';var anchor=card.querySelector('.mode-tag-group');card.insertBefore(details,anchor);['sep-pattern','pattern-clusters','sep-etterna','ett-skill-bars','sep-graph','body-graph-wrap'].forEach(function(id){var n=document.getElementById(id);if(n)details.appendChild(n);});}}else if(card&&details){while(details.firstChild)card.insertBefore(details.firstChild,details);details.remove();}" +
            "var cover=document.getElementById('mma-comp-cover-layer'),meta=document.getElementById('mma-comp-meta'),summary=document.getElementById('mma-comp-summary'),chart=document.getElementById('mma-comp-chart');" +
            "if(card&&" + Bool(layout == "companella") + "){window.__mmaCompSignature='';if(!cover){cover=document.createElement('div');cover.id='mma-comp-cover-layer';cover.className='mma-comp-cover-layer';card.insertBefore(cover,card.firstChild);}if(!meta){meta=document.createElement('div');meta.id='mma-comp-meta';meta.className='mma-comp-meta';meta.innerHTML=" + Js("<div class='mma-comp-map'><span id='mma-comp-mapper'>" + ManiaMapAnalyzerOverlay.UiText.Get("Ожидание данных карты", "Waiting for beatmap data") + "</span><span class='mma-comp-muted' id='mma-comp-version'></span></div><div class='mma-comp-numbers'><div id='mma-comp-stats'>BPM — · OD — · HP —</div><div class='mma-comp-muted' id='mma-comp-ids'>Set — · Map —</div></div>") + ";card.insertBefore(meta,details||card.querySelector('.mode-tag-group'));}" +
            "if(!summary){summary=document.createElement('div');summary.id='mma-comp-summary';summary.className='mma-comp-summary';summary.innerHTML=" + Js("<div class='mma-comp-summary-item mma-comp-summary-rating'><span class='mma-comp-summary-label'>Star rating</span><strong class='mma-comp-summary-value' id='mma-summary-star'>—</strong><small class='mma-comp-summary-note' id='mma-summary-star-meta'>LN — · Keys —</small></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>BPM</span><strong class='mma-comp-summary-value' id='mma-summary-bpm'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Set</span><strong class='mma-comp-summary-value' id='mma-summary-set'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Map</span><strong class='mma-comp-summary-value' id='mma-summary-map'>—</strong></div><div class='mma-comp-summary-item mma-comp-summary-dan'><span class='mma-comp-summary-label'>RC DAN</span><strong class='mma-comp-summary-value' id='mma-summary-rc-dan'>—</strong><small class='mma-comp-summary-note' id='mma-summary-rc-dan-value'>—</small></div><div class='mma-comp-summary-item mma-comp-summary-dan'><span class='mma-comp-summary-label'>LN DAN</span><strong class='mma-comp-summary-value' id='mma-summary-ln-dan'>—</strong></div>") + ";card.insertBefore(summary,details||card.querySelector('.mode-tag-group'));}" +
            "if(!chart){chart=document.createElement('div');chart.id='mma-comp-chart';chart.className='mma-comp-chart';card.insertBefore(chart,details||card.querySelector('.mode-tag-group'));}}else{if(cover)cover.remove();if(meta)meta.remove();if(summary)summary.remove();if(chart)chart.remove();document.documentElement.style.removeProperty('--mma-comp-cover');}})();";
    }

    private static string BuildObserverScript(bool overlayMode)
    {
        var mapper = JsonSerializer.Serialize(ManiaMapAnalyzerOverlay.UiText.Get("Автор карты: ", "Mapped by "));
        var mapperEmpty = JsonSerializer.Serialize(ManiaMapAnalyzerOverlay.UiText.Get("Автор —", "Mapper —"));
        return "(function(){var card=document.querySelector('.main-card');" +
            "function send(m){try{if(typeof invokeCSharpAction==='function')invokeCSharpAction(m);else if(window.chrome&&chrome.webview)chrome.webview.postMessage(m);}catch(_){}}" +
            (overlayMode ? BuildResizeHandleScript() : BuildResizeHandleCleanupScript()) +
            "if(!card)return;" +
            "function report(){var r=card.getBoundingClientRect(),s=getComputedStyle(card),d=Math.max(1,window.devicePixelRatio||1);send('mma:size:'+Math.ceil(r.width*d)+','+Math.ceil(r.height*d)+','+((parseFloat(s.borderTopLeftRadius)||0)*d));}" +
            "function syncSummary(){var source=document.getElementById('rework-star'),target=document.getElementById('mma-summary-star'),meta=document.getElementById('mma-summary-star-meta'),diff=document.getElementById('rework-diff'),rcDan=document.getElementById('mma-summary-rc-dan'),lnDan=document.getElementById('mma-summary-ln-dan'),caption=document.getElementById('est-diff-caption'),rcDanValue=document.getElementById('mma-summary-rc-dan-value');if(source&&target){var unit=source.getAttribute('data-unit')||'SR';target.textContent=((source.textContent||'—').trim()||'—')+(unit?' '+unit:'');}var sourceMeta=document.getElementById('rework-meta');if(sourceMeta&&meta)meta.textContent=(sourceMeta.textContent||'').replace(/\\s+/g,' ').trim()||'LN — · Keys —';var raw=diff?(diff.textContent||'').trim():'',normalized=raw.replace(/\\s+/g,' ').trim(),parts=raw.split(/\\s*\\|\\|\\s*|\\r?\\n\\s*(?=(?:[<>]\\s+)?(?:LN\\b|(?:[A-Za-z][\\w./-]*\\s+)?LN\\b))/i).map(function(value){return value.trim();}).filter(Boolean);if(parts.length<2)parts=normalized.split(/(?=\\s+[<>]\\s+LN(?:\\s+DAN)?\\b)/i);if(parts.length<2){var explicit=normalized.match(/^(.+?)\\s+(?=LN(?:\\s+DAN)?\\b)(.+)$/i),looksRc=/^(?:[<>]\\s*|(?:rc|reform|rework|regular|intro|alpha|beta|gamma|delta|epsilon|zeta|eta|theta|iota|kappa|cloverwisp|emik|thaumiel)\\b)/i;if(explicit&&looksRc.test(explicit[1].trim()))parts=[explicit[1],explicit[2]];}var looksLn=/^(?:[<>]\\s*)?(?:ln\\b|(?:[A-Za-z][\\w./-]*\\s+)+ln\\b)/i,rc=parts.length>0?parts[0].trim():'',ln=parts.length>1?parts.slice(1).join(' || ').trim():'';if(parts.length<2&&looksLn.test(rc)){ln=rc;rc='';}var missing=/^(?:—|-|n\\/a|none)$/i,clean=function(value){return value.replace(/^(?:rc|ln)\\b\\s*(?:dan\\b)?\\s*[:\\-]?\\s*/i,'').replace(/^([<>])\\s*ln\\b\\s*(?:dan\\b)?\\s*[:\\-]?\\s*/i,'$1 ').trim();};rc=clean(rc);ln=clean(ln);var comparable=function(value){return clean(value).toLowerCase();};if(!rc||missing.test(rc))rc='—';if(!ln||missing.test(ln)||comparable(rc)===comparable(ln))ln='—';if(rcDan)rcDan.textContent=rc;if(lnDan)lnDan.textContent=ln;if(rcDanValue){var match=String(caption&&caption.textContent||'').match(/\\((?:RC\\s*)?(-?\\d+(?:[.,]\\d+)?)\\)/i),numeric=match?Number(match[1].replace(',','.')):NaN;rcDanValue.textContent=isFinite(numeric)?'≈ '+numeric.toFixed(2):'—';}}" +
            "function syncComp(){if(!document.documentElement.classList.contains('mma-layout-companella'))return;syncSummary();var chart=document.getElementById('mma-comp-chart');if(!chart)return;var p=document.getElementById('pattern-clusters'),e=document.getElementById('ett-skill-bars'),list=p&&!p.hidden?p:(e&&!e.hidden?e:null),graph=document.getElementById('body-graph-wrap');chart.hidden=!!(!list&&graph&&!graph.hidden);var rows=list?Array.prototype.slice.call(list.children):[],items=[];rows.forEach(function(row){if(row.classList.contains('empty')||row.classList.contains('skeleton'))return;var label=row.querySelector('.cluster-label,.ett-skill-label'),value=row.querySelector('.cluster-subtype,.ett-skill-head'),fill=row.querySelector('.cluster-fill,.ett-skill-fill');if(!label||!fill)return;var raw=fill.style.getPropertyValue('--bar-width')||getComputedStyle(fill).getPropertyValue('--bar-width')||fill.style.width||'0',pct=parseFloat(raw);if(!isFinite(pct))pct=0;items.push({label:(label.textContent||'—').trim(),value:value?(value.textContent||'').trim():'',pct:Math.max(2,Math.min(100,pct))});});items=items.slice(0,8);var sig=items.map(function(x){return x.label+'|'+x.value+'|'+x.pct;}).join('~');if(sig===window.__mmaCompSignature)return;window.__mmaCompSignature=sig;chart.textContent='';chart.style.setProperty('--mma-comp-count',String(Math.max(1,items.length)));var colors=['#dedee1','#58b8f0','#5fd56b','#ffae5c','#ae5be2','#ef5d72','#f4d95f','#66cdd0'];items.forEach(function(item,i){var col=document.createElement('div');col.className='mma-comp-column';var box=document.createElement('div');box.className='mma-comp-barbox';var bar=document.createElement('div');bar.className='mma-comp-bar';bar.style.setProperty('--mma-value',item.pct+'%');bar.style.setProperty('--mma-color',colors[i%colors.length]);box.appendChild(bar);var value=document.createElement('div');value.className='mma-comp-number';value.textContent=item.value||Math.round(item.pct);var label=document.createElement('div');label.className='mma-comp-label';label.textContent=item.label;col.appendChild(box);col.appendChild(label);col.appendChild(value);chart.appendChild(col);});}" +
            "function num(o,n){if(!o)return null;for(var i=0;i<n.length;i++){var v=Number(o[n[i]]);if(isFinite(v))return v;}return null;}function fmt(v){return v===null||!isFinite(v)?'':(Math.round(v*10)/10).toString();}function bpm(b,s){var x=b.bpm||b.BPM||(s&&(s.bpm||s.BPM));if(x&&typeof x==='object'){var lo=num(x,['min','minimum','lowest']),hi=num(x,['max','maximum','highest']),cur=num(x,['common','base','current']);if(lo!==null&&hi!==null&&Math.abs(lo-hi)>.1)return fmt(lo)+'–'+fmt(hi)+' BPM';if(cur!==null)return fmt(cur)+' BPM';if(hi!==null)return fmt(hi)+' BPM';if(lo!==null)return fmt(lo)+' BPM';}x=Number(x);return isFinite(x)&&x>0?fmt(x)+' BPM':'BPM —';}" +
            "function update(d){if(!document.documentElement.classList.contains('mma-layout-companella'))return;var b=d&&d.beatmap;if(!b)return;var md=b.metadata||{},s=b.stats||{},mapper=String(b.mapper||md.mapper||md.creator||'').trim(),version=String(b.version||md.difficulty||md.version||'').trim(),mapEl=document.getElementById('mma-comp-mapper'),verEl=document.getElementById('mma-comp-version'),statsEl=document.getElementById('mma-comp-stats'),idsEl=document.getElementById('mma-comp-ids');if(mapEl)mapEl.textContent=mapper?" + mapper + "+mapper:" + mapperEmpty + ";if(verEl)verEl.textContent=version?' · ['+version+']':'';var bt=bpm(b,s),od=num(s,['OD','od','overallDifficulty']),hp=num(s,['HP','hp','drainRate']),parts=[bt];if(od!==null)parts.push('OD '+fmt(od));if(hp!==null)parts.push('HP '+fmt(hp));if(statsEl)statsEl.textContent=parts.join(' · ');var mapId=b.id||b.beatmapId||'',setId=b.set||b.setId||b.beatmapSetId||'';if(idsEl)idsEl.textContent='Set '+(setId||'—')+' · Map '+(mapId||'—');var be=document.getElementById('mma-summary-bpm'),se=document.getElementById('mma-summary-set'),me=document.getElementById('mma-summary-map');if(be)be.textContent=bt.replace(/\\s*BPM$/i,'')||'—';if(se)se.textContent=setId||'—';if(me)me.textContent=mapId||'—';var identity=String(mapId||setId||((md.artist||b.artist||'')+'-'+(md.title||b.title||'')+'-'+version));if(identity&&identity!==window.__mmaCoverId){window.__mmaCoverId=identity;document.documentElement.style.setProperty('--mma-comp-cover','url(\"http://'+location.host+'/files/beatmap/background?ts='+encodeURIComponent(identity)+'\")');}}" +
            "function queue(){if(window.__mmaCompFrame)return;window.__mmaCompFrame=requestAnimationFrame(function(){window.__mmaCompFrame=0;syncComp();});}" +
            "if(!window.__mmaLauncherBound){window.__mmaLauncherBound=true;if(" + Bool(overlayMode) + "){function resizeDirection(e){if(e.target&&e.target.closest&&e.target.closest('.mma-resize-handle'))return '';var r=card.getBoundingClientRect(),d=Math.max(1,window.devicePixelRatio||1),edge=Math.max(10,Math.min(14,12/d));if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)return '';var n=e.clientY-r.top<=edge,s=r.bottom-e.clientY<=edge,w=e.clientX-r.left<=edge,ee=r.right-e.clientX<=edge;return (n?'n':s?'s':'')+(w?'w':ee?'e':'');}document.addEventListener('mousedown',function(e){if(e.button!==0)return;var resizeHandle=e.target&&e.target.closest&&e.target.closest('.mma-resize-handle');if(resizeHandle){markResizeGesture();e.preventDefault();return;}if(Date.now()<(window.__mmaResizeGestureUntil||0)){e.preventDefault();return;}var direction=resizeDirection(e);if(direction){markResizeGesture();send('mma:resize:'+direction);}else send('mma:drag');},true);document.addEventListener('wheel',function(e){if(!e.ctrlKey)return;e.preventDefault();var now=Date.now();if(now-(window.__mmaScaleWheelAt||0)<160)return;window.__mmaScaleWheelAt=now;send('mma:scale:'+(e.deltaY<0?'5':'-5'));},{capture:true,passive:false});}window.addEventListener('resize',report);if(window.ResizeObserver)new ResizeObserver(report).observe(card);new MutationObserver(function(){report();queue();}).observe(card,{attributes:true,subtree:true,childList:true,characterData:true});}" +
            BuildPlayWatcherScript() +
            "syncComp();report();setTimeout(function(){syncComp();report();},120);setTimeout(function(){syncComp();report();},600);})();";
    }

    private static string BuildPlayWatcherScript() =>
        "if(!window.__mmaPlayWatcherBound){" +
        "window.__mmaPlayWatcherBound=true;" +
        "var stateNumber=null,stateName='',stateKnown=false,gameplay=false,focusKnown=false,gameFocused=false;" +
        "function own(o,k){return !!o&&Object.prototype.hasOwnProperty.call(o,k);}" +
        "function normalizeStateName(value){return String(value==null?'':value).toLowerCase().replace(/[^a-z]/g,'');}" +
        "function applyState(d){" +
        "var state=d&&d.state&&typeof d.state==='object'?d.state:null,hasNumber=own(state,'number'),hasName=own(state,'name');" +
        "if(hasNumber){var rawNumber=state.number,parsed=Number(rawNumber);stateNumber=rawNumber===null||rawNumber===undefined||String(rawNumber).trim()===''||!isFinite(parsed)?null:parsed;}" +
        "if(hasName)stateName=normalizeStateName(state.name);" +
        "if(hasNumber||hasName){var knownNumber=hasNumber&&stateNumber!==null,knownName=hasName&&!!stateName;" +
        "if(knownNumber||knownName){stateKnown=true;gameplay=knownNumber?stateNumber===2:(stateName==='play'||stateName==='gameplay'||stateName==='playing');send('mma:play:'+(gameplay?'1':'0'));}}" +
        "var game=d&&d.game;" +
        "if(own(game,'focused')&&typeof game.focused==='boolean'){var nextFocused=game.focused;if(!focusKnown||gameFocused!==nextFocused)send('mma:focus:'+(nextFocused?'1':'0'));gameFocused=nextFocused;focusKnown=true;document.documentElement.classList.toggle('mma-osu-focused',gameFocused);}" +
        "}" +
        "function connect(){" +
        "document.documentElement.classList.remove('mma-osu-focused');stateNumber=null;stateName='';stateKnown=false;gameplay=false;focusKnown=false;gameFocused=false;" +
        "var ws=new WebSocket('ws://'+location.host+'/websocket/v2?l='+encodeURIComponent(window.COUNTER_PATH||location.pathname));" +
        "window.__mmaPlayWatcherSocket=ws;" +
        "ws.onopen=function(){ws.send('applyFilters:'+JSON.stringify([{field:'state',keys:['number','name']},{field:'game',keys:['focused']},{field:'beatmap',keys:['artist','title','version','mapper','id','set','setId','beatmapSetId','metadata','stats','bpm']}]))};" +
        "ws.onmessage=function(e){var d;try{d=JSON.parse(e.data);}catch(_){return;}try{update(d);}catch(_){}try{applyState(d);}catch(_){}};" +
        "ws.onclose=function(){if(focusKnown)send('mma:focus:0');document.documentElement.classList.remove('mma-osu-focused');stateNumber=null;stateName='';stateKnown=false;gameplay=false;focusKnown=false;gameFocused=false;if(document.documentElement.classList.contains('launcher-overlay-host'))setTimeout(connect,1000);};" +
        "}" +
        "connect();}";

    private static string BuildResizeHandleScript()
    {
        var handleCss = JsonSerializer.Serialize(
            "html.launcher-overlay-host .mma-resize-handle{" +
            "position:fixed!important;z-index:2147483647!important;display:block!important;box-sizing:border-box!important;" +
            "margin:0!important;padding:0!important;border:0!important;outline:0!important;background:transparent!important;" +
            "opacity:0!important;pointer-events:auto!important;user-select:none!important;-webkit-user-select:none!important;" +
            "-webkit-user-drag:none!important;touch-action:none!important;" +
            "}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='n']{top:0;left:20px;right:20px;height:20px;cursor:ns-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='s']{bottom:0;left:20px;right:20px;height:20px;cursor:ns-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='w']{left:0;top:20px;bottom:20px;width:20px;cursor:ew-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='e']{right:0;top:20px;bottom:20px;width:20px;cursor:ew-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='nw']{left:0;top:0;width:20px;height:20px;cursor:nwse-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='se']{right:0;bottom:0;width:20px;height:20px;cursor:nwse-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='ne']{right:0;top:0;width:20px;height:20px;cursor:nesw-resize!important;}" +
            "html.launcher-overlay-host .mma-resize-handle[data-direction='sw']{left:0;bottom:0;width:20px;height:20px;cursor:nesw-resize!important;}");

        return
            "function markResizeGesture(){var until=Date.now()+500;window.__mmaResizeGestureUntil=until;if(window.__mmaResizeGestureTimer)clearTimeout(window.__mmaResizeGestureTimer);window.__mmaResizeGestureTimer=setTimeout(function(){if(Date.now()>=(window.__mmaResizeGestureUntil||0))window.__mmaResizeGestureUntil=0;window.__mmaResizeGestureTimer=null;},550);}" +
            "function clearResizeGesture(){window.__mmaResizeGestureUntil=0;if(window.__mmaResizeGestureTimer){clearTimeout(window.__mmaResizeGestureTimer);window.__mmaResizeGestureTimer=null;}}" +
            "function ensureResizeHandles(){" +
            "var root=document.body||document.documentElement;if(!root)return;" +
            "var style=document.getElementById('mma-resize-handle-style');" +
            "if(!style){style=document.createElement('style');style.id='mma-resize-handle-style';style.textContent=" + handleCss + ";(document.head||root).appendChild(style);}" +
            "var directions=['n','s','e','w','nw','ne','se','sw'];" +
            "directions.forEach(function(direction){" +
            "var handle=document.querySelector('.mma-resize-handle[data-direction=\"'+direction+'\"]');" +
            "if(!handle){handle=document.createElement('div');handle.className='mma-resize-handle';handle.setAttribute('data-direction',direction);handle.setAttribute('aria-hidden','true');root.appendChild(handle);}" +
            "if(handle.__mmaResizeBound)return;handle.__mmaResizeBound=true;" +
            "var begin=function(e){" +
            "if(e.button!==undefined&&e.button!==0)return;" +
            "e.preventDefault();e.stopPropagation();if(e.stopImmediatePropagation)e.stopImmediatePropagation();" +
            "var now=Date.now();if(now-(handle.__mmaResizeLast||0)<120)return;handle.__mmaResizeLast=now;" +
            "markResizeGesture();" +
            "send('mma:resize:'+direction);" +
            "};" +
            "handle.addEventListener('pointerdown',begin,{capture:true,passive:false});" +
            "handle.addEventListener('mousedown',begin,{capture:true,passive:false});" +
            "});}" +
            "ensureResizeHandles();" +
            "if(!window.__mmaResizeGestureCleanupBound){window.__mmaResizeGestureCleanupBound=true;['pointerup','pointercancel','mouseup'].forEach(function(type){document.addEventListener(type,clearResizeGesture,true);});window.addEventListener('blur',clearResizeGesture,true);}" +
            "if(!window.__mmaResizeHandlesObserver&&window.MutationObserver){" +
            "window.__mmaResizeHandlesObserver=new MutationObserver(function(){ensureResizeHandles();});" +
            "window.__mmaResizeHandlesObserver.observe(document.documentElement,{childList:true,subtree:true});" +
            "}";
    }

    private static string BuildResizeHandleCleanupScript() =>
        "if(window.__mmaResizeHandlesObserver){window.__mmaResizeHandlesObserver.disconnect();window.__mmaResizeHandlesObserver=null;}" +
        "document.querySelectorAll('.mma-resize-handle').forEach(function(handle){handle.remove();});" +
        "var resizeStyle=document.getElementById('mma-resize-handle-style');if(resizeStyle)resizeStyle.remove();" +
        "window.__mmaResizeGestureUntil=0;if(window.__mmaResizeGestureTimer){clearTimeout(window.__mmaResizeGestureTimer);window.__mmaResizeGestureTimer=null;}";

    private static string BuildCompanellaChartEnhancementV2()
    {
        return "(function(){" +
            "var card=document.querySelector('.main-card'),version='numeric-values-v1';if(!card)return;" +
            "function clean(value){return String(value==null?'':value).replace(/\\s+/g,' ').trim();}" +
            "function token(value){var match=String(value||'').match(/[+-]?(?:\\d+(?:[.,]\\d+)?|[.,]\\d+)/);return match?match[0]:'';}" +
            "function formatAbsolute(value){var text=String(value||'').replace(',','.').trim(),number=Number(text);if(!isFinite(number))return '';var fraction=(text.match(/[.](\\d+)/)||[])[1]||'';return number.toFixed(Math.min(2,fraction.length));}" +
            "function absoluteValue(row,value,raw){var simple=/^[+-]?(?:\\d+(?:[.,]\\d+)?|[.,]\\d+)$/;if(simple.test(raw)&&raw.indexOf('%')<0)return formatAbsolute(raw);if(value&&value.classList.contains('ett-skill-head')&&!/%/.test(raw)){var head=token(raw);if(head)return formatAbsolute(head);}var nodes=[value,row],names=['data-value','data-amount','data-score','data-rating','data-absolute','aria-valuenow'];for(var i=0;i<nodes.length;i++){var node=nodes[i];if(!node)continue;for(var j=0;j<names.length;j++){var candidate=clean(node.getAttribute(names[j]));if(candidate&&simple.test(candidate)&&candidate.indexOf('%')<0)return formatAbsolute(candidate);}}return '';};" +
            "function detailValue(raw,absolute){if(!raw||raw==='-'||raw==='—')return '';if(absolute){var rawToken=token(raw),rawNumber=Number(String(rawToken).replace(',','.')),absoluteNumber=Number(String(absolute).replace(',','.'));if(rawToken&&isFinite(rawNumber)&&isFinite(absoluteNumber)&&Math.abs(rawNumber-absoluteNumber)<0.000001){var rest=clean(raw.replace(rawToken,'').replace(/[()\\[\\]{}]/g,'')).trim();if(!rest||/^[%:|,.;·\\-–—\\s]*$/.test(rest))return '';return rest;}}return raw;}" +
            "function sync(){if(!document.documentElement.classList.contains('mma-layout-companella'))return;var chart=document.getElementById('mma-comp-chart');if(!chart)return;var p=document.getElementById('pattern-clusters'),e=document.getElementById('ett-skill-bars'),list=p&&!p.hidden?p:(e&&!e.hidden?e:null),graph=document.getElementById('body-graph-wrap');if(!list&&graph&&!graph.hidden){chart.hidden=true;chart.textContent='';chart.removeAttribute('data-mma-chart-version');return;}chart.hidden=false;var rows=list?Array.prototype.slice.call(list.children).filter(function(row){return!row.classList.contains('empty')&&!row.classList.contains('skeleton');}):[],columns=Array.prototype.slice.call(chart.children).filter(function(column){return column.classList.contains('mma-comp-column');});if(!columns.length)return;columns.slice(0,8).forEach(function(column,index){var row=rows[index],value=row&&row.querySelector('.cluster-subtype,.ett-skill-head'),bar=column.querySelector('.mma-comp-bar'),number=column.querySelector('.mma-comp-number');if(!bar||!number)return;if(number.getAttribute('data-mma-chart-number')==='true')return;var raw=clean(value&&value.textContent),rawWidth=bar.style.getPropertyValue('--mma-value')||getComputedStyle(bar).getPropertyValue('--mma-value')||'0',parsed=parseFloat(rawWidth);if(!isFinite(parsed))parsed=0;var rawPct=Math.max(0,Math.min(100,parsed)),absolute=absoluteValue(row,value,raw),display=absolute||Math.round(rawPct)+'%',detail=detailValue(raw,absolute);number.className='mma-comp-number'+(rawPct<24?' mma-comp-number-outside':'');number.setAttribute('data-mma-chart-number','true');number.textContent=display;number.title=display;bar.appendChild(number);var oldDetail=column.querySelector('.mma-comp-detail');if(oldDetail)oldDetail.remove();if(detail){var detailEl=document.createElement('div');detailEl.className='mma-comp-detail';detailEl.textContent=detail;detailEl.title=detail;column.appendChild(detailEl);}});chart.setAttribute('data-mma-chart-version',version);}" +
            "function queue(){if(window.__mmaNumericCompFrame)return;window.__mmaNumericCompFrame=requestAnimationFrame(function(){window.__mmaNumericCompFrame=0;sync();});}" +
            "if(!window.__mmaNumericCompBound){window.__mmaNumericCompBound=true;new MutationObserver(function(){queue();}).observe(card,{attributes:true,subtree:true,childList:true,characterData:true});}sync();})();";
    }

    private static string FixedHeight(string selector, int height, double scale) =>
        "html.mma-layout-default " + selector + ",html.mma-layout-custom " + selector + "{height:" + Px(height, scale) + "!important;min-height:" + Px(height, scale) + "!important;max-height:" + Px(height, scale) + "!important;}";
    private static string Px(double value, double scale) => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(value, scale);
    private static string Bool(bool value) => value ? "true" : "false";
}

public sealed record PresentationScripts(
    string SetupScript,
    string ObserverScript,
    string FullscreenSetupScript,
    string FullscreenObserverScript);
