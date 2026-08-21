namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayScoreSummary
{
    public ReplayScoreSummary(
        int perfect,
        int great,
        int good,
        int ok,
        int meh,
        int miss,
        int combo,
        int maxCombo,
        double accuracy)
    {
        Perfect = perfect;
        Great = great;
        Good = good;
        Ok = ok;
        Meh = meh;
        Miss = miss;
        Combo = combo;
        MaxCombo = maxCombo;
        Accuracy = accuracy;
    }

    public int Perfect
    {
        get;
    }
    public int Great
    {
        get;
    }
    public int Good
    {
        get;
    }
    public int Ok
    {
        get;
    }
    public int Meh
    {
        get;
    }
    public int Miss
    {
        get;
    }
    public int Combo
    {
        get;
    }
    public int MaxCombo
    {
        get;
    }
    public double Accuracy
    {
        get;
    }

    public int TotalHits => Perfect + Great + Good + Ok + Meh + Miss;
}

public static class ReplayScoreCalculator
{
    /// <summary>
    /// Fidelity gate: exact judgement counts and combo are mandatory.
    /// Accuracy is reference-only (ScoreV2/lazer differences) and uses
    /// the fixture's declared client/ruleset policy.
    /// </summary>
    public static ReplayScoreSummary Summarize(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        ReplayScorePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        ArgumentNullException.ThrowIfNull(policy);

        int perfect = 0, great = 0, good = 0, ok = 0, meh = 0, miss = 0;
        int combo = 0, maxCombo = 0;

        foreach (JudgedHitEvent hit in judgedHits)
        {
            switch (hit.Judgement)
            {
                case ReplayJudgement.Perfect:
                    perfect++;
                    break;
                case ReplayJudgement.Great:
                    great++;
                    break;
                case ReplayJudgement.Good:
                    good++;
                    break;
                case ReplayJudgement.Ok:
                    ok++;
                    break;
                case ReplayJudgement.Meh:
                    meh++;
                    break;
                case ReplayJudgement.Miss:
                    miss++;
                    break;
            }

            if (hit.Judgement == ReplayJudgement.Miss)
            {
                combo = 0;
            }
            else
            {
                combo++;
                maxCombo = Math.Max(maxCombo, combo);
            }
        }

        double accuracy = policy.CalculateAccuracy(perfect, great, good, ok, meh, miss);
        return new ReplayScoreSummary(perfect, great, good, ok, meh, miss, combo, maxCombo, accuracy);
    }

    public static bool ValidateFidelityGate(
        ReplayScoreSummary actual,
        ReplayScoreSummary expected,
        double accuracyTolerance = 0.01)
    {
        if (actual.Perfect != expected.Perfect
            || actual.Great != expected.Great
            || actual.Good != expected.Good
            || actual.Ok != expected.Ok
            || actual.Meh != expected.Meh
            || actual.Miss != expected.Miss
            || actual.MaxCombo != expected.MaxCombo)
        {
            return false;
        }

        return Math.Abs(actual.Accuracy - expected.Accuracy) <= accuracyTolerance;
    }
}

public sealed record ReplayScorePolicy
{
    /// <summary>
    /// Stable classic score weights (320/300/200/100/50/0). Matches the
    /// judgement buckets above: Perfect=MAX, Great=300 etc. Accuracy is
    /// weighted/max — do not use it as a fidelity gate; use judgement counts
    /// + maxCombo, with accuracy only within a per-fixture tolerance.
    /// </summary>
    public static ReplayScorePolicy StableClassic
    {
        get;
    } = new(
        perfectWeight: 320,
        greatWeight: 300,
        goodWeight: 200,
        okWeight: 100,
        mehWeight: 50,
        missWeight: 0);

    public ReplayScorePolicy(
        int perfectWeight,
        int greatWeight,
        int goodWeight,
        int okWeight,
        int mehWeight,
        int missWeight)
    {
        PerfectWeight = perfectWeight;
        GreatWeight = greatWeight;
        GoodWeight = goodWeight;
        OkWeight = okWeight;
        MehWeight = mehWeight;
        MissWeight = missWeight;
    }

    public int PerfectWeight
    {
        get;
    }
    public int GreatWeight
    {
        get;
    }
    public int GoodWeight
    {
        get;
    }
    public int OkWeight
    {
        get;
    }
    public int MehWeight
    {
        get;
    }
    public int MissWeight
    {
        get;
    }

    public double CalculateAccuracy(int perfect, int great, int good, int ok, int meh, int miss)
    {
        int total = perfect + great + good + ok + meh + miss;
        if (total == 0)
        {
            return 0;
        }

        double weighted = perfect * PerfectWeight
            + great * GreatWeight
            + good * GoodWeight
            + ok * OkWeight
            + meh * MehWeight
            + miss * MissWeight;

        double max = total * PerfectWeight;
        return weighted / max;
    }
}
