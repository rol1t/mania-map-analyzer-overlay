using System;
using System.Collections.Generic;
using System.Linq;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class HeadlessSnapshotConverter
{
    private static readonly IReadOnlyDictionary<string, string> _skillLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["skills.overall"] = "Overall",
            ["skills.stream"] = "Stream",
            ["skills.jumpstream"] = "Jumpstream",
            ["skills.handstream"] = "Handstream",
            ["skills.stamina"] = "Stamina",
            ["skills.jackspeed"] = "JackSpeed",
            ["skills.chordjack"] = "Chordjack",
            ["skills.technical"] = "Technical"
        };

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

    public static AnalysisSnapshot WithReplayAnalysis(
        AnalysisSnapshot current,
        AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);

        var composed = new ComposedWidgetSnapshot(
            "replay-post-play",
            result.Outcome,
            result.Metrics.Select(metric => new ResolvedSemanticMetric(
                metric.Key,
                metric.Value,
                new AnalysisMetricProvenance(
                    "replay-post-play",
                    metric.Key,
                    result.EngineId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    result.RequestedAlgorithm,
                    result.ActualAlgorithm,
                    result.Outcome))),
            result.Diagnostics);

        var replay = BuildReplay(composed);
        if (replay is not null && current.Replay is not null)
        {
            // Preserve live context that is not part of exact timing metrics
            // while replacing provisional timing data.
            replay = replay with
            {
                MapProgressMs = current.Replay.MapProgressMs,
                Score = current.Replay.Score,
                Accuracy = current.Replay.Accuracy,
                RecentOffsets = current.Replay.RecentOffsets
            };
        }

        return current with
        {
            Replay = replay
        };
    }

    public static AnalysisSnapshot FromComposed(
        TosuBeatmapSnapshot beatmap,
        TosuGameplayState? gameplay,
        ComposedWidgetSnapshot composed)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(composed);

        var difficulty = BuildDifficulty(beatmap, composed);
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

        var replay = BuildReplay(composed);

        return new AnalysisSnapshot
        {
            SchemaVersion = AnalysisSnapshot.CurrentSchemaVersion,
            SourceId = composed.Metrics.Values.FirstOrDefault()?.Provenance.EngineId ?? "headless",
            Beatmap = beatmapSnapshot,
            Gameplay = gameplaySnapshot,
            Difficulty = difficulty,
            Ranks = ranks,
            Skills = skills,
            Replay = replay
        };
    }

    private static DifficultySnapshot BuildDifficulty(
        TosuBeatmapSnapshot beatmap,
        ComposedWidgetSnapshot composed)
    {
        double? star = TryGetDouble(composed, "difficulty.star")
            ?? beatmap.Metadata.StarRating;
        // Some analyzer pipelines expose the same ratio under
        // pattern.lnPercent rather than difficulty.lnPercent. Keep the
        // presentation contract canonical so LN DAN is not hidden merely
        // because the selected engine used the pattern metric namespace.
        double? lnPercent = TryGetDouble(composed, "difficulty.lnPercent")
            ?? TryGetDouble(composed, "pattern.lnPercent");
        int? keys = TryGetInt(composed, "difficulty.keys")
            ?? ToKeyCount(beatmap.Metadata.CircleSize);
        var rawLabel = TryGetString(composed, "difficulty.label") ?? string.Empty;
        // MMA's difficulty.label can contain a DAN category such as
        // "Reform ...", not a star-rating label. Keep that value available
        // to BuildRanks, but never render a category inside Star Rating.
        var label = IsNumericStarLabel(rawLabel) ? rawLabel : string.Empty;
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

    /// <summary>
    /// Enriches an already calculated snapshot when Tosu finishes populating
    /// metadata for the same map. Analyzer-derived values remain authoritative;
    /// Tosu only fills fields that the first calculation did not provide.
    /// </summary>
    public static AnalysisSnapshot WithLatestBeatmapMetadata(
        AnalysisSnapshot current,
        TosuBeatmapSnapshot beatmap)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(beatmap);

        if (!string.IsNullOrWhiteSpace(current.Beatmap.Id)
            && !string.IsNullOrWhiteSpace(beatmap.Identity.Id)
            && !string.Equals(current.Beatmap.Id, beatmap.Identity.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cannot enrich a snapshot with metadata from another beatmap.", nameof(beatmap));
        }

        var metadata = beatmap.Metadata;
        var latestBeatmap = current.Beatmap with
        {
            Id = PreferLatest(beatmap.Identity.Id, current.Beatmap.Id),
            SetId = PreferLatest(beatmap.Identity.SetId, current.Beatmap.SetId),
            Artist = PreferLatest(metadata.Artist, current.Beatmap.Artist),
            Title = PreferLatest(metadata.Title, current.Beatmap.Title),
            Version = PreferLatest(metadata.Version, current.Beatmap.Version),
            Mapper = PreferLatest(metadata.Mapper, current.Beatmap.Mapper),
            BpmLabel = metadata.Bpm?.ToString("0.##") ?? current.Beatmap.BpmLabel,
            OverallDifficulty = metadata.OverallDifficulty ?? current.Beatmap.OverallDifficulty,
            HealthDrain = metadata.HealthDrain ?? current.Beatmap.HealthDrain,
            BackgroundUrl = PreferLatest(metadata.BackgroundPath, current.Beatmap.BackgroundUrl)
        };
        var latestDifficulty = current.Difficulty with
        {
            StarRating = current.Difficulty.StarRating ?? metadata.StarRating,
            Keys = current.Difficulty.Keys ?? ToKeyCount(metadata.CircleSize)
        };

        return current with
        {
            Beatmap = latestBeatmap,
            Difficulty = latestDifficulty
        };
    }

    private static string PreferLatest(string latest, string current) =>
        string.IsNullOrWhiteSpace(latest) ? current : latest;

    private static int? ToKeyCount(double? circleSize)
    {
        if (!circleSize.HasValue || !double.IsFinite(circleSize.Value))
        {
            return null;
        }

        var rounded = (int)Math.Round(circleSize.Value, MidpointRounding.AwayFromZero);
        return rounded is >= 1 and <= 18 && Math.Abs(circleSize.Value - rounded) < 0.001
            ? rounded
            : null;
    }

    private static IReadOnlyList<RankEstimate> BuildRanks(ComposedWidgetSnapshot composed)
    {
        var ranks = new List<RankEstimate>();
        var rcLabel = TryGetString(composed, "dan.rc.label");
        var rcNumeric = TryGetDouble(composed, "dan.rc.numeric");
        var lnLabel = TryGetString(composed, "dan.ln.label");

        if (string.IsNullOrWhiteSpace(rcLabel))
        {
            var difficultyLabel = TryGetString(composed, "difficulty.label");
            var labels = difficultyLabel?.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            rcLabel = labels?.FirstOrDefault();
            lnLabel ??= labels?.Length > 1 ? string.Join(" || ", labels.Skip(1)) : null;
        }

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

        var lnPercent = TryGetDouble(composed, "difficulty.lnPercent") ?? TryGetDouble(composed, "pattern.lnPercent");
        var hasLn = lnPercent.HasValue && lnPercent.Value > 0;

        if (!string.IsNullOrWhiteSpace(lnLabel) && hasLn)
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
                Label = GetSkillLabel(id),
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
                    Label = GetSkillLabel(entry.Key),
                    ValueLabel = value?.ToString("0.##") ?? entry.Value.Metric.Value.ToString() ?? string.Empty,
                    Value = value,
                    NormalizedValue = value.HasValue ? Math.Clamp(value.Value, 0, 100) : 0,
                    Detail = entry.Value.Metric.Value.ToString() ?? string.Empty
                });
            }
        }

        return skills.Take(8).ToArray();
    }

    private static string GetSkillLabel(string id) =>
        _skillLabels.TryGetValue(id, out var label)
            ? label
            : id.StartsWith("skills.", StringComparison.OrdinalIgnoreCase)
                ? id["skills.".Length..]
                : id;

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

    private static bool IsNumericStarLabel(string value)
    {
        var firstToken = value
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return firstToken is not null
            && double.TryParse(
                firstToken,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out _);
    }

    private static ReplayOverlaySnapshot? BuildReplay(ComposedWidgetSnapshot composed)
    {
        bool hasReplay = composed.Metrics.Keys.Any(key => key.StartsWith("replay.", StringComparison.OrdinalIgnoreCase));
        if (!hasReplay)
        {
            return null;
        }

        var ur = TryGetDouble(composed, "replay.timing.ur");
        var mean = TryGetDouble(composed, "replay.timing.meanMs");
        var median = TryGetDouble(composed, "replay.timing.medianMs");
        var sd = TryGetDouble(composed, "replay.timing.sdMs");
        var early = TryGetInt(composed, "replay.timing.earlyCount");
        var late = TryGetInt(composed, "replay.timing.lateCount");
        var sample = TryGetInt(composed, "replay.timing.sampleCount");

        // Columns: replay.column.{n}.biasMs / ur / missCount / hitCount
        var columns = new List<ReplayColumnSnapshot>();
        for (int column = 0; column < 18; column++)
        {
            bool hasColumn = composed.Metrics.ContainsKey($"replay.column.{column}.biasMs")
                || composed.Metrics.ContainsKey($"replay.column.{column}.ur")
                || composed.Metrics.ContainsKey($"replay.column.{column}.missCount");
            if (!hasColumn)
            {
                continue;
            }

            columns.Add(new ReplayColumnSnapshot
            {
                Column = column,
                BiasMs = TryGetDouble(composed, $"replay.column.{column}.biasMs"),
                Ur = TryGetDouble(composed, $"replay.column.{column}.ur"),
                MissCount = TryGetInt(composed, $"replay.column.{column}.missCount"),
                HitCount = TryGetInt(composed, $"replay.column.{column}.hitCount")
            });
        }

        // Sections: replay.section.{i}.accuracy / ur — discover by scanning
        var sections = new List<ReplaySectionSnapshot>();
        for (int index = 0; index < 32; index++)
        {
            bool hasSection = composed.Metrics.ContainsKey($"replay.section.{index}.accuracy");
            if (!hasSection)
            {
                continue;
            }

            sections.Add(new ReplaySectionSnapshot
            {
                Index = index,
                Accuracy = TryGetDouble(composed, $"replay.section.{index}.accuracy"),
                Ur = TryGetDouble(composed, $"replay.section.{index}.ur")
            });
        }

        // Insights: any replay.insights.* or replay.pattern.* as generic
        var insights = new List<ReplayInsightSnapshot>();
        foreach (var entry in composed.Metrics.Where(metric => metric.Key.StartsWith("replay.insights.", StringComparison.OrdinalIgnoreCase) || metric.Key.StartsWith("replay.pattern.", StringComparison.OrdinalIgnoreCase)))
        {
            string value = TryGetString(composed, entry.Key) ?? entry.Value.Metric.Value.ToString() ?? string.Empty;
            insights.Add(new ReplayInsightSnapshot
            {
                Code = entry.Key,
                Message = value,
                Confidence = null
            });
        }

        // Also map aggregated insight count if present
        if (composed.Metrics.TryGetValue("replay.insights.count", out var countMetric) && countMetric.Metric.Value.TryGetDouble(out double countValue))
        {
            // Keep as signal even if no per-insight strings
        }

        // Fidelity/reason from diagnostics: look for replay.fidelity.*
        string fidelity = string.Empty;
        string reason = string.Empty;
        var fidelityDiagnostic = composed.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code.StartsWith("replay.fidelity.", StringComparison.OrdinalIgnoreCase) || diagnostic.Code == "replay.fidelity.partial" || diagnostic.Code == "replay.fidelity.exact");
        if (fidelityDiagnostic is not null)
        {
            fidelity = fidelityDiagnostic.Code;
            reason = fidelityDiagnostic.Message;
        }

        if (ur is null && mean is null && columns.Count == 0 && sections.Count == 0 && insights.Count == 0)
        {
            // Has replay prefix but no concrete values — still return empty container for visibility
            return new ReplayOverlaySnapshot { Fidelity = fidelity, Reason = reason };
        }

        return new ReplayOverlaySnapshot
        {
            Ur = ur,
            MeanMs = mean,
            MedianMs = median,
            SdMs = sd,
            EarlyCount = early,
            LateCount = late,
            SampleCount = sample,
            Fidelity = fidelity,
            Reason = reason,
            Columns = columns,
            Sections = sections,
            Insights = insights
        };
    }
}
