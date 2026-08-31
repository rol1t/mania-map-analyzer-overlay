# DAN estimates

The overlay treats DAN values as chart estimates, not player ranks or proof of
having cleared a course. The production analyzer remains the MIT-licensed
[ManiaMapAnalyser](https://github.com/LeoBlackMT/osumania_map_analyser) runtime.

## Reference comparison

The public [Mania Tracker methodology](https://mania-tracker.com/dan-estimates)
was reviewed on 2026-08-31. Mania Tracker also uses ManiaMapAnalyser Mixed for
4K regular charts, the analyzer's LN interval table for established 4K LN
charts, and Sunny interval tables for 6K/7K. Running this application's current
installed Mixed pipeline against the article's reference beatmaps produced:

| Beatmap | Mania Tracker | This application | Result |
| --- | --- | --- | --- |
| 2675345 Cyberia lyr3 | 4 (`4.04`) | Reform 4 mid (`4.04`) | same level and raw value |
| 1561270 A Lasting Promise | 7 (`6.93`) | Reform 7 mid (`6.93`) | same level and raw value |
| 1887434 Aquaris | 10 (`10.05`) | Reform 10 mid (`10.05`) | same level and raw value |
| 3729620 Makiba | alpha++ (`11.44`) | Alpha high (`11.44`) | same level; `high` is the `++` tier |
| 2793593 far in the blue sky | delta+ (`14.25`) | Delta mid/high (`14.25`) | same level; `mid/high` is the `+` tier |
| 3629313 R.I.P. | 4K LN 10 | LN 10 mid | same LN table level |
| 538161 C18H27NO3 | 7K LN 9 | LN 9 mid | same LN table level |
| 4596114 G e n g a o z o | 7K gamma+ | Regular Gamma mid/high | same Sunny table tier |
| 1325722 Cartoon Candy | 6K terra+ | `> Regular 9 high` | different: Mania Tracker extends the upstream 6K RC table |

The parenthesized values are the article's published `rawDan` calibration
values. The check is intentionally a developer/reference comparison and is
not a network dependency in production.

## Important differences

1. **Primary ladder.** Mania Tracker classifies a chart as LN at a 45% hold
   ratio, except 7K where the threshold is 37.5%. The application now marks
   exactly one `RankEstimate.IsPrimary` with the same rule. The other estimate
   may remain visible as a reference, but it is not presented as a second
   authoritative chart identity.
2. **Tier vocabulary.** ManiaMapAnalyser returns `low`, `mid/low`, `mid`,
   `mid/high`, and `high`. Mania Tracker writes those as `--`, `-`, neutral,
   `+`, and `++`. The Companella presenter uses only that compact marker in
   visible text. The unabridged analyzer verdict remains available through the
   element's accessible label and tooltip.
3. **Low 4K LN.** Mania Tracker has an additional nearest-neighbour model for
   LN charts below the upstream table's LN 5 floor. This application does not
   copy or claim that site-specific, unlicensed calibration model; below-table results remain
   explicit boundaries rather than fabricated precision.
4. **6K extension.** Mania Tracker extends the 6K RC ladder beyond Regular 9
   into Terra/Celestial/Mystery/Nihility/Finish. The upstream
   ManiaMapAnalyser release used by this application currently stops at the
   Regular 9 boundary. This difference is displayed honestly.
5. **Confidence and abuse guards.** Mania Tracker applies additional
   calibration, vibro detection, and confidence damping around its stored
   chart verdicts. Those site-specific adjustments are not part of the
   upstream analyzer contract used here.
6. **Player DAN is out of scope.** Mania Tracker converts score accuracy into
   pass credit and aggregates a player's best clears by skill. The overlay
   analyzes the selected chart only and does not infer a player's DAN.

## Badge artwork

The project owner explicitly requested the original Mania Hub badge catalog.
The 97 files are packaged locally rather than hotlinked, then embedded into the
WebView as data URIs so windowed and fullscreen/native presentations use the
same offline artwork. Separate mappings are retained for 4K Reform, 4K LN, 6K
regular/LN and 7K regular/LN ladders. The compact `--`, `-`, `+`, and `++`
tier marker and the full analyzer label remain presentation-owned additions;
the image does not alter the calculated estimate.

The exact source commit and file mapping are recorded in
[`assets/overlay/runtime/dan-images/ATTRIBUTION.md`](../assets/overlay/runtime/dan-images/ATTRIBUTION.md).
This inclusion has an unresolved redistribution risk: the public Mania Hub
repository had no root software or artwork license when the files were copied,
and its source states that several 6K glyphs are traced from course
backgrounds. The artwork is not relicensed under this application's MIT
license. Downstream packages should retain the attribution and independently
review the rights before redistribution.

If an estimate has no matching packaged badge, the Companella presenter falls
back to its local text medallion rather than displaying an incorrect key-mode
or course-family image.
