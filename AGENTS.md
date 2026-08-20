# Agent workflow

## Model roles

- Use GPT-5.6 Luna with Max reasoning as the primary agent for conversation, repository inspection, implementation, routine testing, and ordinary code review.
- Use GPT-5.6 Terra with High or XHigh reasoning for bounded repository analysis, debugging hypotheses, medium-complexity planning, and review of Luna's proposed implementation.
- Use GPT-5.6 Sol with Ultra reasoning only for architecture, difficult cross-platform behavior, security-sensitive update or installer logic, large refactors, unresolved systemic bugs, and final review before a major release.
- Keep the primary Luna agent responsible for the user conversation, implementation, verification, and final handoff.
- Give Terra and Sol bounded tasks with a concrete question or review scope. Do not delegate routine implementation to them.
- If the requested model or reasoning level is unavailable, report that limitation explicitly before using a different configuration.

## Escalation policy

- Luna may complete small and medium tasks directly when the required behavior is clear.
- Delegate to Terra when analysis is materially useful but does not require architectural judgment.
- Escalate from Terra to Sol only when the task affects architecture, public contracts, security, release integrity, or multiple platform adapters.
- Do not invoke Sol for documentation, CSS-only changes, localization, simple UI corrections, straightforward bug fixes, or routine builds.
- Avoid repeatedly delegating the same task between models.

## Verification

- Review the complete diff before reporting completion.
- Run the appropriate build and tests for every source, build-script, packaging, or test change.
- Do not claim that runtime behavior works unless it was tested or explicitly left for user testing.
- Keep architectural decisions and acceptance of major changes with the primary agent, using Sol's review only when the escalation policy requires it.

## Repository conventions

- The application uses Avalonia. Do not restore or introduce WinForms code.
- Use `ManiaMapAnalyzerOverlay.sln` as the main solution.
- Keep UI, platform integration, business logic, and external analyser integration separated.
- Do not place business logic in Avalonia views or code-behind.
- Store CSS presets, templates, images, and other editable assets in dedicated files instead of hardcoding them in C#.
- Preserve English and Russian localization for all user-facing UI text.
- Windows is the primary supported platform. Linux support is experimental and must not be broken by Windows-specific changes.
- Isolate Windows-specific behavior behind platform services or adapters.

## Repository safety

- Preserve unrelated user changes in the working tree.
- Do not commit, push, merge, tag, or publish a release without explicit user authorization.
- Stage only files belonging to the current task.
- Do not restore legacy project names, WinForms code, deleted documentation, or removed files unless explicitly requested.
- Never include downloaded tosu binaries, credentials, tokens, local settings, or generated user data in Git.
