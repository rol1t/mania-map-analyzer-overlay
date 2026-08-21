export async function classifyCompanellaDifficulty({ msdValues, interludeStar, sunnyStar }) {
    if (!msdValues || !Number.isFinite(interludeStar) || !Number.isFinite(sunnyStar)) {
        throw new Error("Fixture Companella inputs are incomplete.");
    }

    return {
        estDiff: "Reform 7 mid/high",
        numericDifficulty: 7.25,
        numericDifficultyHint: "fixture",
        danLabel: "7",
        variant: "+",
        confidence: 0.8,
    };
}
