using System;
using System.Globalization;

namespace ManiaMapAnalyzerOverlay
{
    internal static class OverlayStyleBuilder
    {
        internal static string Pixels(double value, double scale)
        {
            int pixels = Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
            return pixels.ToString(CultureInfo.InvariantCulture) + "px";
        }

        internal static string BuildBaseScaleCss(double scale)
        {
            string scaleText = scale.ToString("0.00", CultureInfo.InvariantCulture);
            return
                "html.launcher-overlay-host{--mma-host-scale:" + scaleText + ";}" +
                "html.launcher-overlay-host .card{border-radius:" + Pixels(16, scale) + "!important;padding:" + Pixels(12, scale) + "!important;}" +
                "html.launcher-overlay-host .main-card{gap:" + Pixels(8, scale) + "!important;}" +
                "html.launcher-overlay-host .status-row{gap:" + Pixels(10, scale) + "!important;margin-bottom:" + Pixels(8, scale) + "!important;}" +
                "html.launcher-overlay-host .status{font-size:" + Pixels(15, scale) + "!important;}" +
                "html.launcher-overlay-host .star-block{gap:" + Pixels(6, scale) + "!important;}" +
                "html.launcher-overlay-host .star-left{gap:" + Pixels(4, scale) + "!important;}" +
                "html.launcher-overlay-host .star-meta{font-size:" + Pixels(14, scale) + "!important;}" +
                "html.launcher-overlay-host .star-value{font-size:" + Pixels(48, scale) + "!important;padding:" + Pixels(4, scale) + " " + Pixels(10, scale) + "!important;border-radius:" + Pixels(20, scale) + "!important;}" +
                "html.launcher-overlay-host .main-card:not(.bars-none) .star-value:not(.category-mode){font-size:" + Pixels(52, scale) + "!important;padding:" + Pixels(5, scale) + " " + Pixels(12, scale) + "!important;border-radius:" + Pixels(22, scale) + "!important;}" +
                "html.launcher-overlay-host .star-value.category-mode{font-size:" + Pixels(24, scale) + "!important;}" +
                "html.launcher-overlay-host .star-right-group{gap:" + Pixels(5, scale) + "!important;}" +
                "html.launcher-overlay-host .star-right{gap:" + Pixels(3, scale) + "!important;margin-bottom:" + Pixels(2, scale) + "!important;}" +
                "html.launcher-overlay-host .star-subtitle{font-size:" + Pixels(27, scale) + "!important;}" +
                "html.launcher-overlay-host .star-caption{font-size:" + Pixels(12, scale) + "!important;}" +
                "html.launcher-overlay-host .top-right-capsule{font-size:" + Pixels(30, scale) + "!important;padding:" + Pixels(4, scale) + " " + Pixels(10, scale) + "!important;border-radius:" + Pixels(18, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-bars{gap:" + Pixels(6, scale) + "!important;padding-right:" + Pixels(4, scale) + "!important;padding-bottom:" + Pixels(18, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-item{gap:" + Pixels(4, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-label{font-size:" + Pixels(15, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-track{height:" + Pixels(10, scale) + "!important;}" +
                "html.launcher-overlay-host .cluster-subtype{font-size:" + Pixels(13, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-bars{gap:" + Pixels(8, scale) + "!important;padding:" + Pixels(2, scale) + " " + Pixels(4, scale) + " " + Pixels(18, scale) + " 0!important;}" +
                "html.launcher-overlay-host .ett-skill-item{gap:" + Pixels(4, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-label{font-size:" + Pixels(14, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-track{height:" + Pixels(15, scale) + "!important;}" +
                "html.launcher-overlay-host .ett-skill-head{font-size:" + Pixels(11, scale) + "!important;padding:" + Pixels(1, scale) + " " + Pixels(6, scale) + "!important;}" +
                "html.launcher-overlay-host .mode-tag{font-size:" + Pixels(11, scale) + "!important;padding:" + Pixels(2, scale) + " " + Pixels(9, scale) + "!important;}" +
                "html.launcher-overlay-host .pause-count{font-size:" + Pixels(12, scale) + "!important;}";
        }

        internal static string BuildReadableTypographyCss(double scale)
        {
            string small = "clamp(" + Pixels(12, scale) + ",var(--mma-type-small)," + Pixels(16, scale) + ")";
            string body = "clamp(" + Pixels(15, scale) + ",var(--mma-type-body)," + Pixels(20, scale) + ")";
            string heading = "clamp(" + Pixels(18, scale) + ",var(--mma-type-heading)," + Pixels(26, scale) + ")";
            string rating = "clamp(" + Pixels(24, scale) + ",var(--mma-type-rating)," + Pixels(34, scale) + ")";

            return
                "html.mma-layout-default,html.mma-layout-custom{--mma-type-small:2.9vw;--mma-type-body:3.6vw;--mma-type-heading:4.5vw;--mma-type-rating:6.4vw;}" +
                "html.mma-layout-horizontal{--mma-type-small:1.35vw;--mma-type-body:1.75vw;--mma-type-heading:2.25vw;--mma-type-rating:3.1vw;}" +
                "html.mma-layout-companella{--mma-type-small:1.95vw;--mma-type-body:2.55vw;--mma-type-heading:3.15vw;--mma-type-rating:4.15vw;}" +
                "html.launcher-overlay-host .status{font-size:" + body + "!important;}" +
                "html.launcher-overlay-host .star-meta,html.launcher-overlay-host .star-caption{font-size:" + small + "!important;}" +
                "html.launcher-overlay-host .star-subtitle{font-size:" + heading + "!important;}" +
                "html.launcher-overlay-host .star-value.category-mode{font-size:" + heading + "!important;}" +
                "html.launcher-overlay-host .cluster-label,html.launcher-overlay-host .ett-skill-label{font-size:" + body + "!important;}" +
                "html.launcher-overlay-host .cluster-subtype,html.launcher-overlay-host .ett-skill-head,html.launcher-overlay-host .mode-tag,html.launcher-overlay-host .pause-count{font-size:" + small + "!important;}" +
                "html.mma-layout-companella .mma-comp-meta,html.mma-layout-companella .mma-comp-summary-label,html.mma-layout-companella .mma-comp-summary-note,html.mma-layout-companella .mma-comp-number,html.mma-layout-companella .mode-tag{font-size:" + small + "!important;}" +
                "html.mma-layout-companella .mma-comp-summary-value,html.mma-layout-companella .mma-comp-label{font-size:" + body + "!important;}" +
                "html.mma-layout-companella .mma-comp-summary-rating .mma-comp-summary-value{font-size:" + rating + "!important;}";
        }

        internal static string BuildInteractionCss()
        {
            return
                "html.launcher-overlay-host .main-card,html.launcher-overlay-host .main-card *{-webkit-user-select:none!important;user-select:none!important;-webkit-user-drag:none!important;}" +
                "html.launcher-transparent-overlay,html.launcher-transparent-overlay body,html.launcher-transparent-overlay .main-card,html.launcher-transparent-overlay .main-card *{cursor:none!important;}";
        }

        internal static string BuildCompanellaCss(double scale, string width)
        {
            return
                "html.mma-layout-companella{--mma-host-width:" + width + ";}" +
                "html.mma-layout-companella .dashboard{width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;}" +
                "html.mma-layout-companella .main-card,html.mma-layout-companella .main-card.bars-pattern,html.mma-layout-companella .main-card.bars-etterna,html.mma-layout-companella .main-card.bars-etterna.bars-etterna-compact,html.mma-layout-companella .main-card.bars-graph,html.mma-layout-companella .main-card.bars-none,html.mma-layout-companella .main-card.bars-full{display:grid!important;grid-template-columns:minmax(0,1fr)!important;grid-template-rows:auto!important;grid-auto-rows:auto!important;gap:" + Pixels(7, scale) + "!important;align-items:start!important;align-content:start!important;width:var(--mma-host-width)!important;min-width:var(--mma-host-width)!important;max-width:var(--mma-host-width)!important;height:auto!important;min-height:0!important;max-height:none!important;padding:" + Pixels(11, scale) + " " + Pixels(13, scale) + " " + Pixels(28, scale) + "!important;overflow:hidden!important;background:#0a0c12!important;border:" + Pixels(1, scale) + " solid rgba(255,255,255,.17)!important;border-bottom:" + Pixels(3, scale) + " solid #ff4f9b!important;border-radius:" + Pixels(9, scale) + "!important;}" +
                "html.mma-layout-companella .main-card::before,html.mma-layout-companella .main-card::after{display:none!important;content:none!important;opacity:0!important;}" +
                "html.mma-layout-companella .mma-comp-cover-layer{display:block!important;position:absolute!important;inset:0!important;z-index:0!important;border-radius:inherit!important;background-image:linear-gradient(100deg,rgba(6,8,12,.82) 0%,rgba(8,10,16,.77) 58%,rgba(4,6,11,.90) 100%),var(--mma-comp-cover,var(--ma-cover,none))!important;background-size:cover!important;background-position:center!important;background-repeat:no-repeat!important;pointer-events:none!important;}" +
                "html.mma-layout-companella .main-card>.status-row,html.mma-layout-companella .main-card>.mma-comp-meta,html.mma-layout-companella .main-card>.mma-comp-summary,html.mma-layout-companella .main-card>.mma-comp-chart,html.mma-layout-companella .main-card>.mma-host-details,html.mma-layout-companella .main-card>.mode-tag-group,html.mma-layout-companella .main-card>.pause-count{position:relative!important;z-index:1!important;}" +
                "html.mma-layout-companella .triangle-field{opacity:.08!important;}" +
                "html.mma-layout-companella .status-row{grid-column:1!important;grid-row:1!important;min-width:0!important;margin:0!important;padding:0 0 " + Pixels(6, scale) + "!important;border-bottom:" + Pixels(1, scale) + " solid rgba(255,255,255,.15)!important;}" +
                "html.mma-layout-companella .title-icon{display:none!important;}" +
                "html.mma-layout-companella .status{display:block!important;width:100%!important;max-width:100%!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(15, scale) + "!important;font-weight:600!important;color:#f7f7fa!important;letter-spacing:.01em!important;}" +
                "html.mma-layout-companella .mma-comp-meta{grid-column:1!important;grid-row:2!important;display:block!important;min-width:0!important;color:#c8cad4!important;font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(10, scale) + "!important;line-height:1.35!important;}" +
                "html.mma-layout-companella .mma-comp-map{display:block!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;}" +
                "html.mma-layout-companella .mma-comp-numbers{display:none!important;}" +
                "html.mma-layout-companella .mma-comp-muted{color:#9297a8!important;}" +
                "html.mma-layout-companella .mma-comp-summary{grid-column:1!important;grid-row:3!important;display:grid!important;grid-template-columns:1.45fr repeat(4,minmax(0,1fr))!important;gap:" + Pixels(5, scale) + "!important;width:100%!important;min-width:0!important;}" +
                "html.mma-layout-companella .mma-comp-summary-item{display:flex!important;flex-direction:column!important;justify-content:center!important;min-width:0!important;min-height:" + Pixels(48, scale) + "!important;padding:" + Pixels(5, scale) + " " + Pixels(7, scale) + "!important;background:rgba(15,18,27,.66)!important;border:" + Pixels(1, scale) + " solid rgba(255,255,255,.12)!important;border-radius:" + Pixels(6, scale) + "!important;}" +
                "html.mma-layout-companella .mma-comp-summary-label{font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(8, scale) + "!important;line-height:1.1!important;text-transform:uppercase!important;letter-spacing:.08em!important;color:#9297a8!important;}" +
                "html.mma-layout-companella .mma-comp-summary-value{display:block!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(12, scale) + "!important;line-height:1.25!important;font-weight:650!important;color:#f3f4f8!important;}" +
                "html.mma-layout-companella .mma-comp-summary-rating .mma-comp-summary-value{font-size:" + Pixels(21, scale) + "!important;line-height:1!important;color:#ffffff!important;}" +
                "html.mma-layout-companella .mma-comp-summary-note{display:block!important;min-width:0!important;overflow:hidden!important;text-overflow:ellipsis!important;white-space:nowrap!important;font-size:" + Pixels(8, scale) + "!important;line-height:1.15!important;color:#bec1cc!important;}" +
                "html.mma-layout-companella .mma-comp-chart{grid-column:1!important;grid-row:4!important;display:grid!important;grid-template-columns:repeat(var(--mma-comp-count,7),minmax(0,1fr))!important;gap:" + Pixels(6, scale) + "!important;align-items:start!important;width:100%!important;height:auto!important;min-height:" + Pixels(192, scale) + "!important;padding:" + Pixels(3, scale) + " 0 0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-comp-chart[hidden]{display:none!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty{place-items:center!important;color:#9297a8!important;font-size:" + Pixels(10, scale) + "!important;}" +
                "html.mma-layout-companella .mma-comp-chart:empty::after{content:'" + UiText.Get("Нет данных анализа", "No analysis data") + "';}" +
                "html.mma-layout-companella .mma-comp-column{display:grid!important;grid-template-rows:" + Pixels(116, scale) + " auto auto!important;align-content:start!important;gap:" + Pixels(4, scale) + "!important;height:auto!important;min-width:0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-comp-barbox{position:relative!important;display:flex!important;align-items:flex-end!important;width:100%!important;height:" + Pixels(116, scale) + "!important;background:rgba(43,46,57,.78)!important;border:" + Pixels(1, scale) + " solid rgba(255,255,255,.05)!important;border-radius:" + Pixels(3, scale) + "!important;overflow:hidden!important;}" +
                "html.mma-layout-companella .mma-comp-bar{width:100%!important;height:var(--mma-value,2%)!important;min-height:" + Pixels(2, scale) + "!important;background:var(--mma-color,#69ced1)!important;border-radius:" + Pixels(3, scale) + " " + Pixels(3, scale) + " 0 0!important;}" +
                "html.mma-layout-companella .mma-comp-label{grid-row:2!important;display:block!important;min-width:0!important;text-align:center!important;color:#f0f1f5!important;font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(9, scale) + "!important;font-weight:650!important;line-height:1.15!important;white-space:normal!important;overflow:visible!important;overflow-wrap:anywhere!important;}" +
                "html.mma-layout-companella .mma-comp-number{grid-row:3!important;display:block!important;min-width:0!important;text-align:center!important;color:#b9bdca!important;font-family:'Segoe UI',sans-serif!important;font-size:" + Pixels(8, scale) + "!important;font-weight:400!important;line-height:1.2!important;white-space:normal!important;overflow:visible!important;overflow-wrap:anywhere!important;}" +
                "html.mma-layout-companella .mma-host-details{grid-column:1!important;grid-row:5!important;display:grid!important;width:100%!important;min-width:0!important;gap:0!important;padding:0!important;margin:0!important;overflow:visible!important;}" +
                "html.mma-layout-companella .mma-host-details>.full-separator,html.mma-layout-companella .mma-host-details>#pattern-clusters,html.mma-layout-companella .mma-host-details>#ett-skill-bars{display:none!important;}" +
                "html.mma-layout-companella .mma-host-details>.body-graph-wrap{width:100%!important;height:" + Pixels(126, scale) + "!important;margin:0!important;}" +
                "html.mma-layout-companella .star-block{display:none!important;}" +
                "html.mma-layout-companella .star-value.has-unit::after{display:none!important;content:none!important;}" +
                "html.mma-layout-companella .mode-tag-group{left:auto!important;right:" + Pixels(13, scale) + "!important;bottom:" + Pixels(8, scale) + "!important;}" +
                "html.mma-layout-companella .mode-tag{font-size:" + Pixels(9, scale) + "!important;padding:" + Pixels(2, scale) + " " + Pixels(7, scale) + "!important;background:rgba(31,33,43,.90)!important;border-color:rgba(255,255,255,.16)!important;color:#e8e8ed!important;}" +
                "html.mma-layout-companella .pause-count{display:none!important;}" +
                "html.mma-layout-companella .card-overlay{z-index:20!important;}";
        }
    }
}
