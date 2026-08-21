export function applyCompanellaToMixedResult(mixedResult, companellaResult) {
    return {
        ...mixedResult,
        estDiff: `${companellaResult.estDiff} || ${mixedResult.mixedCompanellaPlan.lnDifficulty}`,
        numericDifficulty: companellaResult.numericDifficulty,
        numericDifficultyHint: companellaResult.numericDifficultyHint,
        mixedCompanellaPlan: null,
        mixedMergeApplied: true,
    };
}
