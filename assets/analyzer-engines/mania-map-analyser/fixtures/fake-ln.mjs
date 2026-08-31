export function calculateLN(osuText, speedRate, odFlag, cvtFlag, options) {
    if (options?.withGraph !== true || options?.enableAnalyzeLN !== true) {
        throw new Error("The fixture LN estimator requires graph analysis.");
    }

    return {
        status: "OK",
        graph: {
            times: [0, 1000, 2500, 4000],
            values: [1.2, 2.4, 1.8, 3.1],
        },
    };
}
