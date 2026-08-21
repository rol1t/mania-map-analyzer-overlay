namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayJudgementWindows
{
    /// <summary>
    /// Deterministic placeholder for OD8 stable classic.
    /// osu!mania windows are OD-dependent (e.g. wiki: OD5 MAX≈19ms, OD8 MAX≈16ms, OD10 MAX≈14ms;
    /// 300/200/100/50 map to Perfect/Great/Good/Ok at 16/40/70/100ms for this OD).
    /// Use <see cref="ForOd"/> or a per-fixture <see cref="ReplayJudgeOptions.Windows"/> when
    /// validating golden replays with a different OD.
    /// </summary>
    /// <remarks>
    /// mehMs == missMs (150ms) is intentional: the classifier boundary is the hit window edge.
    /// <see cref="ReplayJudge.JudgeRice"/> already treats absOffset &gt; MissMs as no candidate
    /// and emits a Miss + replay.unmatched_input diagnostic, so a larger missMs would hide
    /// fidelity mismatches instead of surfacing them.
    /// </remarks>
    public static ReplayJudgementWindows Default
    {
        get;
    } = ForOd(8);

    /// <summary>
    /// Builds judgement windows for a specific Overall Difficulty.
    /// Formula mirrors osu!mania stable classic: MAX shrinks with OD.
    /// Kept local so fixtures can pin an explicit ruleset version without
    /// pulling the full osu! client.
    /// </summary>
    public static ReplayJudgementWindows ForOd(double overallDifficulty)
    {
        // Clamp to valid OD range and derive MAX-like window; remaining
        // thresholds are scaled proportionally to the classic OD8 baseline
        // (16/40/70/100/150). This keeps the Default above reproducible and
        // lets golden-fixture tests override via ReplayJudgeOptions.
        double od = Math.Clamp(overallDifficulty, 0, 10);
        int perfectMs = (int)Math.Round(34 - 3 * od + 3 * (od > 5 ? 1 : 0) - (od > 8 ? 1 : 0));
        // Simplify to wiki-consistent anchors: OD0≈22, OD5≈19, OD8≈16, OD10≈14.
        // The closed form above oscillates; pin to known values with linear
        // interpolation between anchors for determinism.
        perfectMs = od switch
        {
            <= 5 => (int)Math.Round(22 - (od / 5) * 3),
            <= 8 => (int)Math.Round(19 - ((od - 5) / 3) * 3),
            _ => (int)Math.Round(16 - ((od - 8) / 2) * 2)
        };

        // Preserve OD8 ratios for the wider windows (40/16=2.5, 70/16=4.375, 100/16=6.25, 150/16=9.375).
        int greatMs = (int)Math.Round(perfectMs * 2.5);
        int goodMs = (int)Math.Round(perfectMs * 4.375);
        int okMs = (int)Math.Round(perfectMs * 6.25);
        int mehMs = (int)Math.Round(perfectMs * 9.375);
        int missMs = mehMs;
        return new ReplayJudgementWindows(perfectMs, greatMs, goodMs, okMs, mehMs, missMs);
    }

    public ReplayJudgementWindows(
        int perfectMs,
        int greatMs,
        int goodMs,
        int okMs,
        int mehMs,
        int missMs)
    {
        if (perfectMs < 0 || greatMs < perfectMs || goodMs < greatMs || okMs < goodMs || mehMs < okMs || missMs < mehMs)
        {
            throw new ArgumentException("Judgement windows must be non-decreasing and non-negative.");
        }

        PerfectMs = perfectMs;
        GreatMs = greatMs;
        GoodMs = goodMs;
        OkMs = okMs;
        MehMs = mehMs;
        MissMs = missMs;
    }

    public int PerfectMs
    {
        get;
    }
    public int GreatMs
    {
        get;
    }
    public int GoodMs
    {
        get;
    }
    public int OkMs
    {
        get;
    }
    public int MehMs
    {
        get;
    }
    public int MissMs
    {
        get;
    }

    public ReplayJudgement Judge(int absOffsetMs)
    {
        if (absOffsetMs <= PerfectMs)
        {
            return ReplayJudgement.Perfect;
        }

        if (absOffsetMs <= GreatMs)
        {
            return ReplayJudgement.Great;
        }

        if (absOffsetMs <= GoodMs)
        {
            return ReplayJudgement.Good;
        }

        if (absOffsetMs <= OkMs)
        {
            return ReplayJudgement.Ok;
        }

        if (absOffsetMs <= MehMs)
        {
            return ReplayJudgement.Meh;
        }

        return ReplayJudgement.Miss;
    }
}
