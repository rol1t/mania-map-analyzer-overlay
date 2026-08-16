using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ManiaMapAnalyzerOverlay
{
    internal sealed partial class MainForm : Form
    {
        private async void OnBrowserNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            try
            {
                Uri uri = browser.Source;
                if (uri != null &&
                    uri.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyLauncherStylesAsync();
                }
            }
            catch
            {
            }
        }

        private string GetOverlayLayoutMode()
        {
            string mode = (launcherSettings.OverlayLayoutMode ?? "default").Trim().ToLowerInvariant();
            if (mode != "default" && mode != "horizontal" && mode != "companella" && mode != "custom")
                mode = "default";
            return mode;
        }

        private int GetOverlayScalePercent()
        {
            return Math.Max(50, Math.Min(180, launcherSettings.OverlayScalePercent));
        }

        private void ShowOverlayStyleDialog()
        {
            CustomCssService.EnsureExists();
            using (var dialog = new OverlayStyleDialog(
                GetOverlayLayoutMode(),
                GetOverlayScalePercent(),
                CustomCssService.GetPath(),
                UiText.IsEnglish))
            {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.Yes)
                {
                    Navigate(DesignUrl);
                    return;
                }
                if (result != DialogResult.OK)
                    return;

                launcherSettings.OverlayLayoutMode = dialog.LayoutMode;
                launcherSettings.OverlayScalePercent = dialog.ScalePercent;
                SaveLauncherSettings();

                Uri uri = browser.Source;
                if (uri != null && uri.AbsolutePath.StartsWith("/ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
                    browser.Reload();
            }
        }

        private async void AdjustOverlayScale(int delta)
        {
            if (!overlayMode)
                return;

            int next = Math.Max(50, Math.Min(180, GetOverlayScalePercent() + delta));
            if (next == launcherSettings.OverlayScalePercent)
                return;

            launcherSettings.OverlayScalePercent = next;
            SaveLauncherSettings();
            try
            {
                await ApplyLauncherStylesAsync();
            }
            catch
            {
            }
        }

        private async Task ApplyLauncherStylesAsync()
        {
            if (!browserReady || browser.CoreWebView2 == null)
                return;

            string layoutMode = GetOverlayLayoutMode();
            string customCss = "";
            string css;
            bool renderSelectedPreset = true;
            double nativeScale = GetOverlayScalePercent() / 100D;
            string defaultWidth = OverlayStyleBuilder.Pixels(475, nativeScale);
            string horizontalWidth = OverlayStyleBuilder.Pixels(920, nativeScale);
            string companellaWidth = OverlayStyleBuilder.Pixels(620, nativeScale);
            if (renderSelectedPreset)
            {
                css =
                    "html,body{width:100%!important;height:100%!important;min-height:0!important;background:transparent!important;overflow:hidden!important;}" +
                    "body{padding:0!important;margin:0!important;}" +
                    ".dashboard{min-height:0!important;margin:0!important;gap:0!important;align-content:start!important;}" +
                    ".card.main-card{margin:0!important;box-shadow:none!important;}" +
                    OverlayStyleBuilder.BuildBaseScaleCss(nativeScale);

                if (string.Equals(layoutMode, "horizontal", StringComparison.Ordinal))
                {
                    css +=
                        "html.mma-layout-horizontal{--mma-host-width:" + horizontalWidth + ";}" +
                        "html.mma-layout-horizontal .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-horizontal .main-card{display:grid!important;grid-template-columns:34% minmax(0,66%)!important;grid-template-rows:auto auto!important;grid-auto-rows:auto!important;column-gap:" + OverlayStyleBuilder.Pixels(20, nativeScale) + "!important;row-gap:" + OverlayStyleBuilder.Pixels(8, nativeScale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:" + OverlayStyleBuilder.Pixels(318, nativeScale) + "!important;max-height:none!important;padding:" + OverlayStyleBuilder.Pixels(14, nativeScale) + " " + OverlayStyleBuilder.Pixels(16, nativeScale) + " " + OverlayStyleBuilder.Pixels(34, nativeScale) + "!important;overflow:hidden!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-pattern,html.mma-layout-horizontal .main-card.bars-etterna,html.mma-layout-horizontal .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-horizontal .main-card.bars-graph,html.mma-layout-horizontal .main-card.bars-none,html.mma-layout-horizontal .main-card.bars-full{height:auto!important;min-height:" + OverlayStyleBuilder.Pixels(318, nativeScale) + "!important;max-height:none!important;}" +
                        "html.mma-layout-horizontal .status-row{grid-column:1/-1!important;grid-row:1!important;margin:0 0 " + OverlayStyleBuilder.Pixels(4, nativeScale) + "!important;}" +
                        "html.mma-layout-horizontal .star-block{grid-column:1!important;grid-row:2!important;align-self:start!important;display:flex!important;flex-direction:column!important;align-items:stretch!important;justify-content:flex-start!important;gap:" + OverlayStyleBuilder.Pixels(14, nativeScale) + "!important;min-width:0!important;}" +
                        "html.mma-layout-horizontal .star-left{width:100%!important;}" +
                        "html.mma-layout-horizontal .star-right-group{width:100%!important;max-width:100%!important;flex:0 0 auto!important;align-items:flex-start!important;justify-content:flex-start!important;}" +
                        "html.mma-layout-horizontal .star-right{text-align:left!important;justify-items:start!important;}" +
                        "html.mma-layout-horizontal .mma-host-details{grid-column:2!important;grid-row:2!important;display:grid!important;grid-auto-rows:auto!important;gap:" + OverlayStyleBuilder.Pixels(8, nativeScale) + "!important;align-content:start!important;min-width:0!important;overflow:visible!important;}" +
                        "html.mma-layout-horizontal .mma-host-details>[hidden]{display:none!important;}" +
                        "html.mma-layout-horizontal .cluster-bars,html.mma-layout-horizontal .ett-skill-bars{height:auto!important;min-height:0!important;max-height:none!important;overflow:visible!important;padding-bottom:" + OverlayStyleBuilder.Pixels(24, nativeScale) + "!important;margin-bottom:0!important;}" +
                        "html.mma-layout-horizontal .body-graph-wrap{width:100%!important;margin:0 auto " + OverlayStyleBuilder.Pixels(24, nativeScale) + "!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none{grid-template-columns:1fr!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .star-block{grid-column:1/-1!important;display:flex!important;flex-direction:row!important;align-items:flex-end!important;justify-content:space-between!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .star-right-group{width:56%!important;max-width:56%!important;}" +
                        "html.mma-layout-horizontal .main-card.bars-none .mma-host-details{display:none!important;}";
                }
                else if (string.Equals(layoutMode, "companella", StringComparison.Ordinal))
                {
                    css += OverlayStyleBuilder.BuildCompanellaCss(nativeScale, companellaWidth);
                }
                else
                {
                    css +=
                        "html.mma-layout-default,html.mma-layout-custom{--mma-host-width:" + defaultWidth + ";}" +
                        "html.mma-layout-default .dashboard,html.mma-layout-custom .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-default .main-card,html.mma-layout-custom .main-card{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                        "html.mma-layout-default .main-card,html.mma-layout-custom .main-card{height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-pattern,html.mma-layout-custom .main-card.bars-pattern{height:" + OverlayStyleBuilder.Pixels(575, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(575, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(575, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-graph,html.mma-layout-custom .main-card.bars-graph{height:" + OverlayStyleBuilder.Pixels(396, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(396, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(396, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-none,html.mma-layout-custom .main-card.bars-none{height:" + OverlayStyleBuilder.Pixels(248, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(248, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(248, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-etterna,html.mma-layout-custom .main-card.bars-etterna{height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-custom .main-card.bars-etterna.bars-etterna-compact{height:" + OverlayStyleBuilder.Pixels(500, nativeScale) + "!important;min-height:" + OverlayStyleBuilder.Pixels(500, nativeScale) + "!important;max-height:" + OverlayStyleBuilder.Pixels(500, nativeScale) + "!important;}" +
                        "html.mma-layout-default .main-card.bars-full,html.mma-layout-custom .main-card.bars-full{height:auto!important;min-height:" + OverlayStyleBuilder.Pixels(540, nativeScale) + "!important;max-height:none!important;}";
                    if (string.Equals(layoutMode, "custom", StringComparison.Ordinal))
                    {
                        try
                        {
                            string cssPath = CustomCssService.GetPath();
                            if (File.Exists(cssPath))
                                customCss = File.ReadAllText(cssPath, System.Text.Encoding.UTF8);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            else
            {
                css =
                    "html{height:100%!important;overflow:hidden!important;}" +
                    "body{height:100%!important;min-height:100%!important;padding:18px!important;overflow:auto!important;}" +
                    ".dashboard{margin:0 auto!important;min-height:0!important;align-content:start!important;}";
            }

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

            var serializer = new JavaScriptSerializer();
            string script =
                "(function(){" +
                "var s=document.getElementById('launcher-host-style');" +
                "if(!s){s=document.createElement('style');s.id='launcher-host-style';document.head.appendChild(s);}" +
                "s.textContent=" + serializer.Serialize(css) + ";" +
                "var c=document.getElementById('launcher-custom-style');" +
                "if(!c){c=document.createElement('style');c.id='launcher-custom-style';document.head.appendChild(c);}" +
                "c.textContent=" + serializer.Serialize(customCss) + ";" +
                "document.documentElement.classList.toggle('launcher-overlay-host',true);" +
                "document.documentElement.classList.toggle('mma-layout-default'," + (layoutMode == "default" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-horizontal'," + (layoutMode == "horizontal" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-companella'," + (layoutMode == "companella" ? "true" : "false") + ");" +
                "document.documentElement.classList.toggle('mma-layout-custom'," + (layoutMode == "custom" ? "true" : "false") + ");" +
                "var card=document.querySelector('.main-card');var details=document.getElementById('mma-host-details');" +
                "if(card&&" + (layoutMode == "horizontal" || layoutMode == "companella" ? "true" : "false") + "){" +
                "if(!details){details=document.createElement('div');details.id='mma-host-details';details.className='mma-host-details';" +
                "var anchor=card.querySelector('.mode-tag-group');card.insertBefore(details,anchor);" +
                "['sep-pattern','pattern-clusters','sep-etterna','ett-skill-bars','sep-graph','body-graph-wrap'].forEach(function(id){var n=document.getElementById(id);if(n)details.appendChild(n);});}}" +
                "else if(card&&details){while(details.firstChild)card.insertBefore(details.firstChild,details);details.remove();}" +
                "var compCover=document.getElementById('mma-comp-cover-layer');var compMeta=document.getElementById('mma-comp-meta');var compSummary=document.getElementById('mma-comp-summary');var compChart=document.getElementById('mma-comp-chart');" +
                "if(card&&" + (layoutMode == "companella" ? "true" : "false") + "){window.__mmaCompSignature='';" +
                "if(!compCover){compCover=document.createElement('div');compCover.id='mma-comp-cover-layer';compCover.className='mma-comp-cover-layer';card.insertBefore(compCover,card.firstChild);}" +
                "if(!compMeta){compMeta=document.createElement('div');compMeta.id='mma-comp-meta';compMeta.className='mma-comp-meta';compMeta.innerHTML=\"<div class='mma-comp-map'><span id='mma-comp-mapper'>" + UiText.Get("Ожидание данных карты", "Waiting for beatmap data") + "</span><span class='mma-comp-muted' id='mma-comp-version'></span></div><div class='mma-comp-numbers'><div id='mma-comp-stats'>BPM — · OD — · HP —</div><div class='mma-comp-muted' id='mma-comp-ids'>Set — · Map —</div></div>\";card.insertBefore(compMeta,details||card.querySelector('.mode-tag-group'));}" +
                "if(!compSummary){compSummary=document.createElement('div');compSummary.id='mma-comp-summary';compSummary.className='mma-comp-summary';compSummary.innerHTML=\"<div class='mma-comp-summary-item mma-comp-summary-rating'><span class='mma-comp-summary-label'>Star rating</span><strong class='mma-comp-summary-value' id='mma-summary-star'>—</strong><small class='mma-comp-summary-note' id='mma-summary-star-meta'>LN — · Keys —</small></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>BPM</span><strong class='mma-comp-summary-value' id='mma-summary-bpm'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Set</span><strong class='mma-comp-summary-value' id='mma-summary-set'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Map</span><strong class='mma-comp-summary-value' id='mma-summary-map'>—</strong></div><div class='mma-comp-summary-item'><span class='mma-comp-summary-label'>Dan</span><strong class='mma-comp-summary-value' id='mma-summary-dan'>—</strong></div>\";card.insertBefore(compSummary,details||card.querySelector('.mode-tag-group'));}" +
                "if(!compChart){compChart=document.createElement('div');compChart.id='mma-comp-chart';compChart.className='mma-comp-chart';card.insertBefore(compChart,details||card.querySelector('.mode-tag-group'));}}" +
                "else{if(compCover)compCover.remove();if(compMeta)compMeta.remove();if(compSummary)compSummary.remove();if(compChart)compChart.remove();document.documentElement.style.removeProperty('--mma-comp-cover');}" +
                "})();";
            await browser.ExecuteScriptAsync(script);

            if (renderSelectedPreset)
            {
                string observerScript =
                    "(function(){" +
                    "var card=document.querySelector('.main-card');if(!card||!window.chrome||!chrome.webview)return;" +
                    "function report(){var r=card.getBoundingClientRect();var s=getComputedStyle(card);var dpr=Math.max(1,window.devicePixelRatio||1);" +
                    "chrome.webview.postMessage('mma:size:'+Math.ceil(r.width*dpr)+','+Math.ceil(r.height*dpr)+','+((parseFloat(s.borderTopLeftRadius)||0)*dpr));}" +
                    "function syncCompanellaSummary(){var source=document.getElementById('rework-star'),target=document.getElementById('mma-summary-star'),meta=document.getElementById('mma-summary-star-meta'),diff=document.getElementById('rework-diff'),dan=document.getElementById('mma-summary-dan');if(source&&target){var unit=source.getAttribute('data-unit')||'SR';target.textContent=((source.textContent||'—').trim()||'—')+(unit?' '+unit:'');}var sourceMeta=document.getElementById('rework-meta');if(sourceMeta&&meta)meta.textContent=(sourceMeta.textContent||'').replace(/\\s+/g,' ').trim()||'LN — · Keys —';if(diff&&dan)dan.textContent=(diff.textContent||'—').trim()||'—';}" +
                    "function syncCompanella(){if(!document.documentElement.classList.contains('mma-layout-companella'))return;syncCompanellaSummary();var chart=document.getElementById('mma-comp-chart');if(!chart)return;" +
                    "var p=document.getElementById('pattern-clusters'),e=document.getElementById('ett-skill-bars');var list=p&&!p.hidden?p:(e&&!e.hidden?e:null);var graph=document.getElementById('body-graph-wrap');var graphOnly=!list&&graph&&!graph.hidden;chart.hidden=!!graphOnly;var rows=list?Array.prototype.slice.call(list.children):[];var items=[];" +
                    "rows.forEach(function(row){if(row.classList.contains('empty')||row.classList.contains('skeleton'))return;var label=row.querySelector('.cluster-label,.ett-skill-label');var value=row.querySelector('.cluster-subtype,.ett-skill-head');var fill=row.querySelector('.cluster-fill,.ett-skill-fill');if(!label||!fill)return;var raw=fill.style.getPropertyValue('--bar-width')||getComputedStyle(fill).getPropertyValue('--bar-width')||fill.style.width||'0';var pct=parseFloat(raw);if(!isFinite(pct))pct=0;pct=Math.max(2,Math.min(100,pct));items.push({label:(label.textContent||'—').trim(),value:value?(value.textContent||'').trim():'',pct:pct});});" +
                    "items=items.slice(0,8);var signature=items.map(function(x){return x.label+'|'+x.value+'|'+x.pct;}).join('~');if(signature===window.__mmaCompSignature)return;window.__mmaCompSignature=signature;chart.textContent='';chart.style.setProperty('--mma-comp-count',String(Math.max(1,items.length)));var colors=['#dedee1','#58b8f0','#5fd56b','#ffae5c','#ae5be2','#ef5d72','#f4d95f','#66cdd0'];" +
                    "items.forEach(function(item,i){var col=document.createElement('div');col.className='mma-comp-column';var box=document.createElement('div');box.className='mma-comp-barbox';var bar=document.createElement('div');bar.className='mma-comp-bar';bar.style.setProperty('--mma-value',item.pct+'%');bar.style.setProperty('--mma-color',colors[i%colors.length]);box.appendChild(bar);var value=document.createElement('div');value.className='mma-comp-number';value.textContent=item.value||Math.round(item.pct);var label=document.createElement('div');label.className='mma-comp-label';label.textContent=item.label;col.appendChild(box);col.appendChild(label);col.appendChild(value);chart.appendChild(col);});}" +
                    "window.__mmaSyncCompanella=syncCompanella;" +
                    "function getNumber(obj,names){if(!obj)return null;for(var i=0;i<names.length;i++){var v=Number(obj[names[i]]);if(isFinite(v))return v;}return null;}" +
                    "function formatNumber(v){if(v===null||!isFinite(v))return '';return (Math.round(v*10)/10).toString();}" +
                    "function formatBpm(bm,stats){var bpm=bm.bpm||bm.BPM||(stats&&(stats.bpm||stats.BPM));if(bpm&&typeof bpm==='object'){var lo=getNumber(bpm,['min','minimum','lowest']);var hi=getNumber(bpm,['max','maximum','highest']);var common=getNumber(bpm,['common','base','current']);if(lo!==null&&hi!==null&&Math.abs(lo-hi)>.1)return formatNumber(lo)+'–'+formatNumber(hi)+' BPM';if(common!==null)return formatNumber(common)+' BPM';if(hi!==null)return formatNumber(hi)+' BPM';if(lo!==null)return formatNumber(lo)+' BPM';}var num=Number(bpm);return isFinite(num)&&num>0?formatNumber(num)+' BPM':'BPM —';}" +
                    "function updateCompanellaMeta(data){if(!document.documentElement.classList.contains('mma-layout-companella'))return;var bm=data&&data.beatmap;if(!bm)return;var md=bm.metadata||{};var stats=bm.stats||{};var mapper=String(bm.mapper||md.mapper||md.creator||'').trim();var version=String(bm.version||md.difficulty||md.version||'').trim();var mapEl=document.getElementById('mma-comp-mapper'),verEl=document.getElementById('mma-comp-version'),statsEl=document.getElementById('mma-comp-stats'),idsEl=document.getElementById('mma-comp-ids');if(mapEl)mapEl.textContent=mapper?'" + UiText.Get("Автор карты: ", "Mapped by ") + "'+mapper:'" + UiText.Get("Автор —", "Mapper —") + "';if(verEl)verEl.textContent=version?' · ['+version+']':'';" +
                    "var bpmText=formatBpm(bm,stats);var od=getNumber(stats,['OD','od','overallDifficulty']),hp=getNumber(stats,['HP','hp','drainRate']);var statParts=[bpmText];if(od!==null)statParts.push('OD '+formatNumber(od));if(hp!==null)statParts.push('HP '+formatNumber(hp));if(statsEl)statsEl.textContent=statParts.join(' · ');var mapId=bm.id||bm.beatmapId||'',setId=bm.set||bm.setId||bm.beatmapSetId||'';if(idsEl)idsEl.textContent='Set '+(setId||'—')+' · Map '+(mapId||'—');var bpmEl=document.getElementById('mma-summary-bpm'),setEl=document.getElementById('mma-summary-set'),mapIdEl=document.getElementById('mma-summary-map');if(bpmEl)bpmEl.textContent=bpmText.replace(/\\s*BPM$/i,'')||'—';if(setEl)setEl.textContent=setId||'—';if(mapIdEl)mapIdEl.textContent=mapId||'—';" +
                    "var title=String(bm.title||md.title||''),artist=String(bm.artist||md.artist||'');var identity=String(mapId||setId||(artist+'-'+title+'-'+version));if(identity&&identity!==window.__mmaCompCoverIdentity){window.__mmaCompCoverIdentity=identity;var cover='url(\"http://'+location.host+'/files/beatmap/background?ts='+encodeURIComponent(identity)+'\")';document.documentElement.style.setProperty('--mma-comp-cover',cover);}}" +
                    "window.__mmaUpdateCompanellaMeta=updateCompanellaMeta;" +
                    "function queueCompanella(){if(window.__mmaCompFrame)return;window.__mmaCompFrame=requestAnimationFrame(function(){window.__mmaCompFrame=0;if(window.__mmaSyncCompanella)window.__mmaSyncCompanella();});}" +
                    "if(!window.__mmaLauncherBound){window.__mmaLauncherBound=true;" +
                    "if(" + (overlayMode ? "true" : "false") + "){document.addEventListener('mousedown',function(e){if(e.button===0)chrome.webview.postMessage('mma:drag');},true);" +
                    "document.addEventListener('wheel',function(e){if(!e.ctrlKey)return;e.preventDefault();var now=Date.now();if(now-(window.__mmaScaleWheelAt||0)<160)return;window.__mmaScaleWheelAt=now;chrome.webview.postMessage('mma:scale:'+(e.deltaY<0?'5':'-5'));},{capture:true,passive:false});}" +
                    "window.addEventListener('resize',report);" +
                    "if(window.ResizeObserver)new ResizeObserver(report).observe(card);" +
                    "new MutationObserver(function(){report();queueCompanella();}).observe(card,{attributes:true,subtree:true,childList:true,characterData:true});}" +
                    "if(!window.__mmaPlayWatcherBound){window.__mmaPlayWatcherBound=true;var lastPlay=null;" +
                    "function connectPlayWatcher(){var ws=new WebSocket('ws://'+location.host+'/websocket/v2?l='+encodeURIComponent(window.COUNTER_PATH||location.pathname));" +
                    "window.__mmaPlayWatcherSocket=ws;" +
                    "ws.onopen=function(){ws.send('applyFilters:'+JSON.stringify([{field:'state',keys:['name']},{field:'beatmap',keys:['artist','title','version','mapper','id','set','setId','beatmapSetId','metadata','stats','bpm']}]));};" +
                    "ws.onmessage=function(e){try{var d=JSON.parse(e.data);var n=String(d&&d.state&&d.state.name||'').toLowerCase().replace(/[^a-z]/g,'');" +
                    "if(window.__mmaUpdateCompanellaMeta)window.__mmaUpdateCompanellaMeta(d);if(!n)return;var playing=n==='play'||n==='gameplay'||n==='playing';if(playing!==lastPlay){lastPlay=playing;chrome.webview.postMessage('mma:play:'+(playing?'1':'0'));}}catch(_){}};" +
                    "ws.onclose=function(){if(document.documentElement.classList.contains('launcher-overlay-host'))setTimeout(connectPlayWatcher,1000);};}" +
                    "connectPlayWatcher();}" +
                    "syncCompanella();report();setTimeout(function(){syncCompanella();report();},120);setTimeout(function(){syncCompanella();report();},600);" +
                    "})();";
                await browser.ExecuteScriptAsync(observerScript);
            }
        }

        private void OnBrowserWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!overlayMode)
                return;

            string message;
            try { message = args.TryGetWebMessageAsString(); }
            catch { return; }

            if (string.Equals(message, "mma:drag", StringComparison.Ordinal))
            {
                BeginOverlayDrag();
                return;
            }

            if (string.Equals(message, "mma:play:1", StringComparison.Ordinal))
            {
                SetOverlaySuppressedByPlay(true);
                return;
            }
            if (string.Equals(message, "mma:play:0", StringComparison.Ordinal))
            {
                SetOverlaySuppressedByPlay(false);
                return;
            }

            const string scalePrefix = "mma:scale:";
            if (message.StartsWith(scalePrefix, StringComparison.Ordinal))
            {
                int delta;
                if (int.TryParse(message.Substring(scalePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
                    AdjustOverlayScale(delta);
                return;
            }

            const string prefix = "mma:size:";
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
                return;

            string[] values = message.Substring(prefix.Length).Split(',');
            int width;
            int height;
            float radius;
            if (values.Length != 3 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) ||
                !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
                return;

            ResizeOverlayToWidget(width, height, radius);
        }

    }
}
