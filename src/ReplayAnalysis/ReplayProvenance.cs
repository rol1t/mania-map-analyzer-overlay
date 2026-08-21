namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record ReplayProvenance
{
    public ReplayProvenance(
        ReplaySourceKind sourceKind,
        ReplayAnalysisFidelity fidelity,
        string rulesetId,
        string rulesetVersion,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(rulesetId))
        {
            throw new ArgumentException("A ruleset id is required.", nameof(rulesetId));
        }

        if (string.IsNullOrWhiteSpace(rulesetVersion))
        {
            throw new ArgumentException("A ruleset version is required.", nameof(rulesetVersion));
        }

        if (fidelity != ReplayAnalysisFidelity.Exact && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for non-exact fidelity.", nameof(reason));
        }

        SourceKind = sourceKind;
        Fidelity = fidelity;
        RulesetId = rulesetId.Trim();
        RulesetVersion = rulesetVersion.Trim();
        Reason = reason?.Trim() ?? string.Empty;
    }

    public ReplaySourceKind SourceKind
    {
        get;
    }

    public ReplayAnalysisFidelity Fidelity
    {
        get;
    }

    public string RulesetId
    {
        get;
    }

    public string RulesetVersion
    {
        get;
    }

    public string Reason
    {
        get;
    }

    public bool IsExact => Fidelity == ReplayAnalysisFidelity.Exact;

    public static ReplayProvenance ExactStable(string rulesetId, string rulesetVersion)
    {
        return new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Exact, rulesetId, rulesetVersion);
    }
}
