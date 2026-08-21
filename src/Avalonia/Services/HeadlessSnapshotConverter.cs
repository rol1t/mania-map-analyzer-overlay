using System;
using System.Collections.Generic;
using System.Linq;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class HeadlessSnapshotConverter
{
    public static AnalysisSnapshot FromAnalysisResult(
        TosuBeatmapSnapshot beatmap,
        TosuGameplayState? gameplay,
        AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(result);
        var composed = new ComposedWidgetSnapshot(
            "headless-single",
            result.Outcome,
            result.Metrics.Select(metric => new ResolvedSemanticMetric(
                metric.Key,
                metric.Value,
                new AnalysisMetricProvenance(
                    "headless-single",
                    metric.Key,
                    result.EngineId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    result.RequestedAlgorithm,
                    result.ActualAlgorithm,
                    result.Outcome))),
            result.Diagnostics);
        return FromComposed(beatmap, gameplay, composed);
    }

    public static AnalysisSnapshot FromComposed(
        TosuBeatmapSnapshot beatmap,
        TosuGameplayState? gameplay,
        ComposedWidgetSnapshot composed)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(composed);

        var difficulty = BuildDifficulty(composed);
        var ranks = BuildRanks(composed);
        var skills = BuildSkills(composed);
        var gameplaySnapshot = BuildGameplay(gameplay);

        var beatmapSnapshot = new BeatmapSnapshot
        {
            Id = beatmap.Identity.Id,
            SetId = beatmap.Identity.SetId,
            Artist = beatmap.Metadata.Artist,
            Title = beatmap.Metadata.Title,
            Version = beatmap.Metadata.Version,
            Mapper = beatmap.Metadata.Mapper,
            BpmLabel = beatmap.Metadata.Bpm?.ToString("0.##") ?? string.Empty,
            OverallDifficulty = beatmap.Metadata.OverallDifficulty,
            HealthDrain = beatmap.Metadata.HealthDrain,
            BackgroundUrl = beatmap.Metadata.BackgroundPath
        };

        return new AnalysisSnapshot
        {
            SchemaVersion = AnalysisSnapshot.CurrentSchemaVersion,
            SourceId = composed.Metrics.Values.FirstOrDefault()?.Provenance.EngineId ?? "headless",
            Beatmap = beatmapSnapshot,
            Gameplay = gameplaySnapshot,
            Difficulty = difficulty,
            Ranks = ranks,
            Skills = skills
        };
    }

    private static DifficultySnapshot BuildDifficulty(ComposedWidgetSnapshot composed)
    {
        double? star = TryGetDouble(composed, "difficulty.star");
        double? lnPercent = TryGetDouble(composed, "difficulty.lnPercent");
        int? keys = TryGetInt(composed, "difficulty.keys");
        var label = TryGetString(composed, "difficulty.label") ?? string.Empty;
        var unit = TryGetString(composed, "difficulty.unit") ?? "SR";

        if (star is null && composed.Metrics.TryGetValue("difficulty.star", out var metric))
        {
            star = metric.Metric.Value.ValueKind is System.Text.Json.JsonValueKind.Number ? metric.Metric.Value.GetDouble() : null;
        }

        return new DifficultySnapshot
        {
            StarRating = star,
            StarLabel = label,
            Unit = unit,
            LnPercent = lnPercent,
            Keys = keys
        };
    }

    private static IReadOnlyList<RankEstimate> BuildRanks(ComposedWidgetSnapshot composed)
    {
        var ranks = new List<RankEstimate>();
        var rcLabel = TryGetString(composed, "dan.rc.label");
        var rcNumeric = TryGetDouble(composed, "dan.rc.numeric");
        var lnLabel = TryGetString(composed, "dan.ln.label");

        if (!string.IsNullOrWhiteSpace(rcLabel) || rcNumeric.HasValue)
        {
            ranks.Add(new RankEstimate
            {
                SystemId = "rc-dan",
                Label = "RC DAN",
                Value = rcLabel ?? string.Empty,
                NumericValue = rcNumeric
            });
        }

        if (!string.IsNullOrWhiteSpace(lnLabel))
        {
            ranks.Add(new RankEstimate
            {
                SystemId = "ln-dan",
                Label = "LN DAN",
                Value = lnLabel,
                NumericValue = null
            });
        }

        // Fallback: if no dan metrics, try generic difficulty label
        if (ranks.Count == 0)
        {
            var diffLabel = TryGetString(composed, "difficulty.label");
            if (!string.IsNullOrWhiteSpace(diffLabel))
            {
                ranks.Add(new RankEstimate { SystemId = "rc-dan", Label = "RC DAN", Value = diffLabel, NumericValue = rcNumeric });
            }
        }

        return ranks;
    }

    private static IReadOnlyList<SkillMetric> BuildSkills(ComposedWidgetSnapshot composed)
    {
        var skills = new List<SkillMetric>();
        var skillIds = new[] { "skills.overall", "skills.stream", "skills.jumpstream", "skills.handstream", "skills.stamina", "skills.jackspeed", "skills.chordjack", "skills.technical" };
        foreach (var id in skillIds)
        {
            if (!composed.Metrics.TryGetValue(id, out var metric))
            {
                continue;
            }

            var value = metric.Metric.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? metric.Metric.Value.GetDouble() : (double?)null;
            var normalized = value.HasValue ? Math.Clamp(value.Value, 0, 100) : 0;
            // Try to get normalized from metric if available via detail?
            skills.Add(new SkillMetric
            {
                Id = id,
                Label = metric.Metric.Id,
                ValueLabel = value?.ToString("0.##") ?? string.Empty,
                Value = value,
                NormalizedValue = normalized,
                Detail = metric.Metric.Value.ToString() ?? string.Empty
            });
        }

        if (skills.Count == 0)
        {
            // Fallback: map any skills.* metrics
            foreach (var entry in composed.Metrics.Where(metric => metric.Key.StartsWith("skills.", StringComparison.OrdinalIgnoreCase)))
            {
                var value = entry.Value.Metric.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? entry.Value.Metric.Value.GetDouble() : (double?)null;
                skills.Add(new SkillMetric
                {
                    Id = entry.Key,
                    Label = entry.Value.Metric.Id,
                    ValueLabel = value?.ToString("0.##") ?? entry.Value.Metric.Value.ToString() ?? string.Empty,
                    Value = value,
                    NormalizedValue = value.HasValue ? Math.Clamp(value.Value, 0, 100) : 0,
                    Detail = entry.Value.Metric.Value.ToString() ?? string.Empty
                });
            }
        }

        return skills.Take(8).ToArray();
    }

    private static GameplaySnapshot BuildGameplay(TosuGameplayState? gameplay)
    {
        if (gameplay is null)
        {
            return new GameplaySnapshot { State = string.Empty, IsPlaying = null, IsPaused = null, IsFocused = null };
        }

        return new GameplaySnapshot
        {
            State = gameplay.Name,
            IsPlaying = gameplay.IsPlaying,
            IsPaused = gameplay.IsPaused,
            IsFocused = null
        };
    }

    private static string? TryGetString(ComposedWidgetSnapshot composed, string metricId)
    {
        if (!composed.Metrics.TryGetValue(metricId, out var metric))
        {
            return null;
        }

        return metric.Metric.Value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => metric.Metric.Value.GetString(),
            System.Text.Json.JsonValueKind.Number => metric.Metric.Value.GetRawText(),
            _ => metric.Metric.Value.ToString()
        };
    }

    private static double? TryGetDouble(ComposedWidgetSnapshot composed, string metricId)
    {
        if (!composed.Metrics.TryGetValue(metricId, out var metric))
        {
            return null;
        }

        return metric.Metric.Value.ValueKind == System.Text.Json.JsonValueKind.Number && metric.Metric.Value.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static int? TryGetInt(ComposedWidgetSnapshot composed, string metricId)
    {
        var value = TryGetDouble(composed, metricId);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }
}
