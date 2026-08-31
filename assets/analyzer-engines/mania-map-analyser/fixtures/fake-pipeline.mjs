export async function runAnalysisPipeline({ rawText, estimatorAlgorithm, options }) {
    if (options.delayMs) {
        await new Promise((resolve) => setTimeout(resolve, options.delayMs));
    }

    const actualEstimatorAlgorithm = String(options.actualAlgorithm || estimatorAlgorithm);
    const isMixedCompanella = estimatorAlgorithm === "Mixed" && actualEstimatorAlgorithm === "Companella";
    const isRiceOnlyFixture = rawText === "rice-only map";
    return {
        rework: {
            star: 5.17,
            lnRatio: isRiceOnlyFixture ? 0 : 0.514,
            columnCount: 4,
            estDiff: isMixedCompanella ? "Sunny base || LN 6 mid/high" : "Sunny base",
            numericDifficulty: null,
            numericDifficultyHint: null,
            mixedCompanellaPlan: isMixedCompanella
                ? { lnRatio: 0.514, lnDifficulty: "LN 6 mid/high" }
                : null,
            graph: isRiceOnlyFixture
                ? {
                    times: [0, 1000, 2000],
                    values: [2.2, 3.3, 2.8],
                }
                : null,
        },
        actualEstimatorAlgorithm,
        sunnyStar: 5.17,
        parsedSummary: {
            metadata: { title: rawText },
            lnRatio: isRiceOnlyFixture ? 0 : 0.514,
            columnCount: 4,
        },
        ettResult: {
            values: {
                Overall: 19.5,
                Stream: 19,
                Jumpstream: 18.5,
                Handstream: 18,
                Stamina: 17.5,
                JackSpeed: 17,
                Chordjack: 16.5,
                Technical: 16,
            },
        },
        interludeStar: 7.5,
        patternReport: {
            LNPercent: isRiceOnlyFixture ? 0 : 0.514,
        },
        receivedOptions: options,
        errors: [],
    };
}
