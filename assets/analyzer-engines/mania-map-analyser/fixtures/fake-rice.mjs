export function calculate(osuText, speedRate, odFlag, cvtFlag, options) {
    if (options?.withGraph !== true) {
        throw new Error("The fixture Rice estimator requires graph analysis.");
    }

    if (!String(cvtFlag || "").includes("HO")) {
        throw new Error("The Rice timeline must be calculated with Hold Off.");
    }

    return {
        status: "OK",
        graph: {
            times: [0, 1000, 2500, 4000],
            values: [3.8, 4.6, 3.9, 5.2],
        },
    };
}
