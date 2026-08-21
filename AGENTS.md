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
- Leave manual runtime testing to the user. Do not automate clicking through the desktop app or perform visual/UI acceptance testing unless the user explicitly asks for it; provide the rebuilt artifact and clearly state what the user should verify.
- Keep architectural decisions and acceptance of major changes with the primary agent, using Sol's review only when the escalation policy requires it.

## Repository conventions

- The application uses Avalonia. Do not restore or introduce WinForms code.
- Use `ManiaMapAnalyzerOverlay.sln` as the main solution.
- Follow the Microsoft C# coding conventions documented in the repository
  `.editorconfig` and in the [official Microsoft guidance](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions).
- Use four spaces (never tabs), Allman braces, one declaration or statement per
  line, file-scoped namespaces, and `using` directives outside namespaces.
- Use PascalCase for types and public members, `I`-prefixed PascalCase for
  interfaces, `_camelCase` for private fields, and camelCase for parameters and
  local variables. Use `var` only when the assigned type is obvious.
- Prefer modern, readable C# (`nameof`, interpolation, collection expressions,
  simple using statements, async/await for I/O) and keep methods focused.
- Catch only exceptions that can be handled. Log every caught exception with
  the application logger and surface user-facing failures; never use an empty
  catch block or silently ignore malformed input.
- Keep UI, platform integration, business logic, and external analyser integration separated.
- Do not place business logic in Avalonia views or code-behind.
- Store CSS presets, templates, images, and other editable assets in dedicated files instead of hardcoding them in C#.
- `README.md` is part of the public preset API. Whenever a user-facing preset workflow or contract changes (discovery paths, manifest fields, templates, CSS/JavaScript resources, selectors, data fields, visibility rules, preview behavior, or limitations), update the user-preset documentation in `README.md` in the same change.
- Preserve English and Russian localization for all user-facing UI text.
- Windows is the primary supported platform. Linux support is experimental and must not be broken by Windows-specific changes.
- Isolate Windows-specific behavior behind platform services or adapters.

## Repository safety

- Preserve unrelated user changes in the working tree.
- Do not commit, push, merge, tag, or publish a release without explicit user authorization.
- Before committing, always ask the user for confirmation and show the diff summary; wait for an explicit `commit`/`approve` reply even if the task is complete.
- The user wants to review every change before it is committed — never commit automatically after finishing a task.
- Stage only files belonging to the current task.
- Do not restore legacy project names, WinForms code, deleted documentation, or removed files unless explicitly requested.
- Never include downloaded tosu binaries, credentials, tokens, local settings, or generated user data in Git.

## C# style verification

- Run `dotnet format ManiaMapAnalyzerOverlay.sln --no-restore` before handing off
  C# changes. Use `--verify-no-changes` in CI or for a final clean-tree check.
- Run `dotnet build ManiaMapAnalyzerOverlay.sln --configuration Release --no-restore --nologo`
  and `dotnet test ManiaMapAnalyzerOverlay.sln --configuration Release --no-build --nologo`.
- Manual runtime and visual testing remain the user's responsibility; do not
  claim those checks were performed by an automated build.
