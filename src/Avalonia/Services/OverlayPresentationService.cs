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
        var companellaWidth = ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(620, scale);

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
        var observer = BuildObserverScript(overlayMode);
        return new PresentationScripts(setup, observer, fullscreenSetup);
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
            "if(!summary){summary=document.createElement('div');summary.id='mma-comp-summary';summary.className='mma-comp-summary';summary.innerHTML=" + Js("<div class='mma-comp-summary-item mma-comp-summary-rating'><span class='mma-comp-summary-label'>Star rating</span><strong class='mma-comp-summary-value' id='mma-summary-star'>—</strong><small class='mma-comp-summary-note' id='mma-summary-star-meta'>LN — · Keys —</small></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>BPM</span><strong class='mma-comp-summary-value' id='mma-summary-bpm'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Set</span><strong class='mma-comp-summary-value' id='mma-summary-set'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Map</span><strong class='mma-comp-summary-value' id='mma-summary-map'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Dan</span><strong class='mma-comp-summary-value' id='mma-summary-dan'>—</strong><small class='mma-comp-summary-note' id='mma-summary-dan-value'>—</small></div>") + ";card.insertBefore(summary,details||card.querySelector('.mode-tag-group'));}" +
            "if(!chart){chart=document.createElement('div');chart.id='mma-comp-chart';chart.className='mma-comp-chart';card.insertBefore(chart,details||card.querySelector('.mode-tag-group'));}}else{if(cover)cover.remove();if(meta)meta.remove();if(summary)summary.remove();if(chart)chart.remove();document.documentElement.style.removeProperty('--mma-comp-cover');}})();";
    }

    private static string BuildObserverScript(bool overlayMode)
    {
        var mapper = JsonSerializer.Serialize(ManiaMapAnalyzerOverlay.UiText.Get("Автор карты: ", "Mapped by "));
        var mapperEmpty = JsonSerializer.Serialize(ManiaMapAnalyzerOverlay.UiText.Get("Автор —", "Mapper —"));
        return "(function(){var card=document.querySelector('.main-card');if(!card)return;" +
            "function send(m){try{if(typeof invokeCSharpAction==='function')invokeCSharpAction(m);else if(window.chrome&&chrome.webview)chrome.webview.postMessage(m);}catch(_){}}" +
            "function report(){var r=card.getBoundingClientRect(),s=getComputedStyle(card),d=Math.max(1,window.devicePixelRatio||1);send('mma:size:'+Math.ceil(r.width*d)+','+Math.ceil(r.height*d)+','+((parseFloat(s.borderTopLeftRadius)||0)*d));}" +
            "function syncSummary(){var source=document.getElementById('rework-star'),target=document.getElementById('mma-summary-star'),meta=document.getElementById('mma-summary-star-meta'),diff=document.getElementById('rework-diff'),dan=document.getElementById('mma-summary-dan'),caption=document.getElementById('est-diff-caption'),danValue=document.getElementById('mma-summary-dan-value');if(source&&target){var unit=source.getAttribute('data-unit')||'SR';target.textContent=((source.textContent||'—').trim()||'—')+(unit?' '+unit:'');}var sourceMeta=document.getElementById('rework-meta');if(sourceMeta&&meta)meta.textContent=(sourceMeta.textContent||'').replace(/\\s+/g,' ').trim()||'LN — · Keys —';if(diff&&dan)dan.textContent=(diff.textContent||'—').trim()||'—';if(danValue){var match=String(caption&&caption.textContent||'').match(/\\((?:RC\\s*)?(-?\\d+(?:[.,]\\d+)?)\\)/i),numeric=match?Number(match[1].replace(',','.')):NaN;danValue.textContent=isFinite(numeric)?'≈ '+numeric.toFixed(2):'—';}}" +
            "function syncComp(){if(!document.documentElement.classList.contains('mma-layout-companella'))return;syncSummary();var chart=document.getElementById('mma-comp-chart');if(!chart)return;var p=document.getElementById('pattern-clusters'),e=document.getElementById('ett-skill-bars'),list=p&&!p.hidden?p:(e&&!e.hidden?e:null),graph=document.getElementById('body-graph-wrap');chart.hidden=!!(!list&&graph&&!graph.hidden);var rows=list?Array.prototype.slice.call(list.children):[],items=[];rows.forEach(function(row){if(row.classList.contains('empty')||row.classList.contains('skeleton'))return;var label=row.querySelector('.cluster-label,.ett-skill-label'),value=row.querySelector('.cluster-subtype,.ett-skill-head'),fill=row.querySelector('.cluster-fill,.ett-skill-fill');if(!label||!fill)return;var raw=fill.style.getPropertyValue('--bar-width')||getComputedStyle(fill).getPropertyValue('--bar-width')||fill.style.width||'0',pct=parseFloat(raw);if(!isFinite(pct))pct=0;items.push({label:(label.textContent||'—').trim(),value:value?(value.textContent||'').trim():'',pct:Math.max(2,Math.min(100,pct))});});items=items.slice(0,8);var sig=items.map(function(x){return x.label+'|'+x.value+'|'+x.pct;}).join('~');if(sig===window.__mmaCompSignature)return;window.__mmaCompSignature=sig;chart.textContent='';chart.style.setProperty('--mma-comp-count',String(Math.max(1,items.length)));var colors=['#dedee1','#58b8f0','#5fd56b','#ffae5c','#ae5be2','#ef5d72','#f4d95f','#66cdd0'];items.forEach(function(item,i){var col=document.createElement('div');col.className='mma-comp-column';var box=document.createElement('div');box.className='mma-comp-barbox';var bar=document.createElement('div');bar.className='mma-comp-bar';bar.style.setProperty('--mma-value',item.pct+'%');bar.style.setProperty('--mma-color',colors[i%colors.length]);box.appendChild(bar);var value=document.createElement('div');value.className='mma-comp-number';value.textContent=item.value||Math.round(item.pct);var label=document.createElement('div');label.className='mma-comp-label';label.textContent=item.label;col.appendChild(box);col.appendChild(label);col.appendChild(value);chart.appendChild(col);});}" +
            "function num(o,n){if(!o)return null;for(var i=0;i<n.length;i++){var v=Number(o[n[i]]);if(isFinite(v))return v;}return null;}function fmt(v){return v===null||!isFinite(v)?'':(Math.round(v*10)/10).toString();}function bpm(b,s){var x=b.bpm||b.BPM||(s&&(s.bpm||s.BPM));if(x&&typeof x==='object'){var lo=num(x,['min','minimum','lowest']),hi=num(x,['max','maximum','highest']),cur=num(x,['common','base','current']);if(lo!==null&&hi!==null&&Math.abs(lo-hi)>.1)return fmt(lo)+'–'+fmt(hi)+' BPM';if(cur!==null)return fmt(cur)+' BPM';if(hi!==null)return fmt(hi)+' BPM';if(lo!==null)return fmt(lo)+' BPM';}x=Number(x);return isFinite(x)&&x>0?fmt(x)+' BPM':'BPM —';}" +
            "function update(d){if(!document.documentElement.classList.contains('mma-layout-companella'))return;var b=d&&d.beatmap;if(!b)return;var md=b.metadata||{},s=b.stats||{},mapper=String(b.mapper||md.mapper||md.creator||'').trim(),version=String(b.version||md.difficulty||md.version||'').trim(),mapEl=document.getElementById('mma-comp-mapper'),verEl=document.getElementById('mma-comp-version'),statsEl=document.getElementById('mma-comp-stats'),idsEl=document.getElementById('mma-comp-ids');if(mapEl)mapEl.textContent=mapper?" + mapper + "+mapper:" + mapperEmpty + ";if(verEl)verEl.textContent=version?' · ['+version+']':'';var bt=bpm(b,s),od=num(s,['OD','od','overallDifficulty']),hp=num(s,['HP','hp','drainRate']),parts=[bt];if(od!==null)parts.push('OD '+fmt(od));if(hp!==null)parts.push('HP '+fmt(hp));if(statsEl)statsEl.textContent=parts.join(' · ');var mapId=b.id||b.beatmapId||'',setId=b.set||b.setId||b.beatmapSetId||'';if(idsEl)idsEl.textContent='Set '+(setId||'—')+' · Map '+(mapId||'—');var be=document.getElementById('mma-summary-bpm'),se=document.getElementById('mma-summary-set'),me=document.getElementById('mma-summary-map');if(be)be.textContent=bt.replace(/\\s*BPM$/i,'')||'—';if(se)se.textContent=setId||'—';if(me)me.textContent=mapId||'—';var identity=String(mapId||setId||((md.artist||b.artist||'')+'-'+(md.title||b.title||'')+'-'+version));if(identity&&identity!==window.__mmaCoverId){window.__mmaCoverId=identity;document.documentElement.style.setProperty('--mma-comp-cover','url(\"http://'+location.host+'/files/beatmap/background?ts='+encodeURIComponent(identity)+'\")');}}" +
            "function queue(){if(window.__mmaCompFrame)return;window.__mmaCompFrame=requestAnimationFrame(function(){window.__mmaCompFrame=0;syncComp();});}" +
            "if(!window.__mmaLauncherBound){window.__mmaLauncherBound=true;if(" + Bool(overlayMode) + "){document.addEventListener('mousedown',function(e){if(e.button===0)send('mma:drag');},true);document.addEventListener('wheel',function(e){if(!e.ctrlKey)return;e.preventDefault();var now=Date.now();if(now-(window.__mmaScaleWheelAt||0)<160)return;window.__mmaScaleWheelAt=now;send('mma:scale:'+(e.deltaY<0?'5':'-5'));},{capture:true,passive:false});}window.addEventListener('resize',report);if(window.ResizeObserver)new ResizeObserver(report).observe(card);new MutationObserver(function(){report();queue();}).observe(card,{attributes:true,subtree:true,childList:true,characterData:true});}" +
            "if(!window.__mmaPlayWatcherBound){window.__mmaPlayWatcherBound=true;var last=null;function connect(){var ws=new WebSocket('ws://'+location.host+'/websocket/v2?l='+encodeURIComponent(window.COUNTER_PATH||location.pathname));window.__mmaPlayWatcherSocket=ws;ws.onopen=function(){ws.send('applyFilters:'+JSON.stringify([{field:'state',keys:['name']},{field:'beatmap',keys:['artist','title','version','mapper','id','set','setId','beatmapSetId','metadata','stats','bpm']}]))};ws.onmessage=function(e){try{var d=JSON.parse(e.data),n=String(d&&d.state&&d.state.name||'').toLowerCase().replace(/[^a-z]/g,'');update(d);if(!n)return;var playing=n==='play'||n==='gameplay'||n==='playing';if(playing!==last){last=playing;send('mma:play:'+(playing?'1':'0'));}}catch(_){}};ws.onclose=function(){if(document.documentElement.classList.contains('launcher-overlay-host'))setTimeout(connect,1000);};}connect();}syncComp();report();setTimeout(function(){syncComp();report();},120);setTimeout(function(){syncComp();report();},600);})();";
    }

    private static string FixedHeight(string selector, int height, double scale) =>
        "html.mma-layout-default " + selector + ",html.mma-layout-custom " + selector + "{height:" + Px(height, scale) + "!important;min-height:" + Px(height, scale) + "!important;max-height:" + Px(height, scale) + "!important;}";
    private static string Px(double value, double scale) => ManiaMapAnalyzerOverlay.OverlayStyleBuilder.Pixels(value, scale);
    private static string Bool(bool value) => value ? "true" : "false";
}

public sealed record PresentationScripts(string SetupScript, string ObserverScript, string FullscreenSetupScript);
