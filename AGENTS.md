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

# OpenCode Multi-Agent Instructions (OpenCode Go subscription)

## Goal

Optimize for the best balance of:

- implementation quality;
- autonomous agent performance;
- speed;
- token efficiency;
- OpenCode Go quota efficiency.

Do not use the most expensive model simply because it is available.
Prefer the cheapest model that is likely to complete the task reliably.

---

## Agent Roles

### 1. Planner — GPT-5.6 Luna High

Primary responsibilities:

- understand the user's request;
- inspect the relevant parts of the repository;
- identify affected components;
- identify architectural constraints;
- identify risks and edge cases;
- produce an implementation plan;
- review completed work when necessary.

The Planner should normally **not implement the feature itself**.

Use the Planner when:

- the task touches several modules;
- architecture decisions are required;
- the task is ambiguous;
- implementation may affect public APIs;
- database/schema changes are involved;
- concurrency, caching, security, networking, or performance are involved;
- the Builder has already failed;
- a substantial refactor is requested.

Do NOT use the Planner for trivial tasks such as:

- renaming variables;
- fixing obvious syntax errors;
- adding a simple DTO;
- changing one configuration value;
- small isolated UI changes.

#### Planner output

The Planner should produce a concise handoff containing:

1. Goal
2. Relevant files/modules
3. Proposed approach
4. Implementation steps
5. Important constraints
6. Validation strategy
7. Known risks

Avoid writing a long essay. The plan exists to help the Builder execute the task efficiently.

---

### 2. Primary Builder — Muse Spark 1.2

Muse Spark is the default implementation agent. Use Muse for approximately **70–80% of coding work**.

Typical tasks:

- implementing features;
- modifying multiple files;
- fixing bugs;
- repository exploration;
- refactoring;
- writing tests;
- running tests;
- debugging test failures;
- implementing an existing plan;
- performing long autonomous coding sessions.

Default reasoning: **High**. Use **XHigh** only when the feature touches many systems, the bug is difficult to reproduce, the repository is unfamiliar, the task requires significant reasoning, several attempts failed, or a major refactor is required. Do not use XHigh automatically.

#### Muse execution rules

Before editing: inspect existing code; identify existing patterns; reuse existing abstractions; avoid unnecessary dependencies.
During implementation: make the smallest coherent change; preserve architecture; avoid speculative refactoring; run tests/build; inspect failures; retry autonomously when cause is clear.
After implementation: run relevant tests; run build when practical; inspect changed files; ensure no unrelated changes.

Provide a short completion report: what changed, tests executed, remaining concerns.

---

### 3. Fast Alternative Builder — DeepSeek V4 Flash

Preferred secondary worker. Use when Muse produced an incorrect solution, appears stuck, a second approach would be useful, task is relatively straightforward, quick diagnosis is useful, or large mechanical code is needed.

Good tasks: boilerplate, tests, CRUD, DTOs, mappings, straightforward fixes, repetitive refactors, config changes, code search/diagnosis, alternative proposals.

Do not escalate to an expensive frontier model before trying DeepSeek if the problem appears implementation-related.

---

### 4. Heavy Coding Escalation — GLM-5.3

Escalation model, **not a default agent**. Use when Muse failed twice, Muse and DeepSeek disagree, bug spans several subsystems, complex repo-wide refactor required, difficult build/toolchain behavior, long-horizon task repeatedly fails, or unusually strong reasoning is needed.

Before invoking GLM-5.3, provide: original task, relevant context, Planner handoff if available, what Muse attempted, what failed, error messages, failing tests. Do not make GLM rediscover prior learnings.

---

### 5. Expert Escalation — Kimi K3

Reserved for the hardest problems. Use only when cheaper models repeatedly failed, root cause remains unclear, deep reasoning across many components is required, architectural alternatives need evaluation, or independent expert opinion is worth the quota cost.

Typical flow: `Muse → DeepSeek → GLM-5.3 → Kimi K3`. Do not jump directly from Muse to Kimi unless exceptionally difficult. Kimi should diagnose/propose the critical solution; hand implementation back to Muse when possible.

---

### 6. Independent Reviewer — Qwen3.8 Max

Primarily acts as an independent reviewer. Use when change is large, architecture changed, solution handles money/permissions/auth/concurrency/persistent data, Luna and Builder disagree, second high-quality opinion is desirable, or confidence is low.

Inspect: correctness, regressions, edge cases, architecture, complexity, API compatibility, race conditions, error handling, test coverage. Do not rewrite entire implementation unless major flaw exists. Return findings ranked Critical/High/Medium/Low; explicitly say if no meaningful issues.

---

## Default Workflow

### Small task (small bug, simple endpoint, DTO, test, minor UI, config)

`Muse Spark 1.2 High → tests/build → done` — No Planner required.

### Medium task (new feature, several files, new service, moderate refactor, non-trivial bug)

`GPT-5.6 Luna High → plan → Muse Spark 1.2 High → tests/build → Luna review if needed`

### Large task (architecture change, new subsystem, repo-wide refactor, complex migration, difficult integration)

`GPT-5.6 Luna High → detailed plan → Muse Spark 1.2 XHigh → tests/build → GPT-5.6 Luna review → fix with Muse`

If Muse cannot complete: `DeepSeek V4 Flash → alternative diagnosis` → if still unresolved: `GLM-5.3` → if exceptionally difficult: `Kimi K3`.

---

## Failure Escalation Policy

Do not endlessly retry the same approach.

- Attempt 1: Muse investigates/implements; if failure and cause is obvious, Muse fixes.
- Attempt 2: Muse tries a substantially different approach; do not repeat superficial patch.
- Attempt 3: Ask DeepSeek V4 Flash for independent diagnosis/alternative/explanation.
- Attempt 4: Use GLM-5.3 with all accumulated context.
- Attempt 5: Use Kimi K3 only if still unresolved or requires expert reasoning.

---

## Review Policy

- No additional review: one-file changes, trivial fixes, tests, boilerplate, config.
- Luna review: features, multi-file refactors, public API changes, database changes, moderately important code.
- Qwen3.8 Max review: critical code, complex architecture, concurrency, auth, persistent state, financial logic, high regression risk.

---

## Context Efficiency

Avoid repeatedly reading the entire repository. Before delegating, create a compact handoff: Task, Relevant files (only likely involved), Current understanding (architectural facts), Attempts, Failure (exact reason), Validation (tests/build commands). Reuse downstream; do not force re-exploration.

---

## Token and Quota Efficiency

Preferred priority (cost vs expected usefulness, not quality ranking):

1. Muse Spark 1.2
2. DeepSeek V4 Flash
3. GPT-5.6 Luna
4. GLM-5.3
5. Qwen3.8 Max
6. Kimi K3

Use expensive models for reasoning bottlenecks, cheap models for implementation volume.

---

## Important Rule

**Planning and difficult reasoning should be separated from routine implementation.**

Example: GPT-5.6 Luna determines architecture/approach, then Muse implements, runs tests, fixes failures, verifies result. If a difficult architectural question appears during implementation, return it to Luna instead of consuming large context exploring alternatives.

---

## Autonomous Behavior

Behave autonomously when next action is obvious. Do not ask for confirmation before reading files, searching repo, running tests/builds, fixing obvious failures, formatting, resolving straightforward compiler errors.

Ask the user only when product requirements are ambiguous, several materially different behaviors are possible, destructive operations are required, credentials/external access required, or decision cannot be derived from repository.

---

## Avoid Overengineering

Prefer `existing pattern → simple change → tests` over `new abstraction → new framework → new dependency → speculative architecture`. Do not introduce abstraction solely for possible future use; do not rewrite unrelated working code; do not expand scope unless necessary.

---

## Testing

After changing code: run narrowest relevant tests first; fix failures; run broader tests when appropriate; run build; report anything not verified. Do not claim success if tests were not run; explain why if they cannot be executed.

---

## Final Response

Be concise. Preferred format:

- Completed — short description
- Changed — important files/components
- Validation — tests/build commands and result
- Notes — unresolved risks or follow-ups

Avoid dumping internal reasoning or lengthy narration.

---

## Routing Summary

```text
TRIVIAL TASK       → Muse High → Done
NORMAL FEATURE     → Luna High — Plan → Muse High — Build → Tests → Done
COMPLEX FEATURE    → Luna High — Plan → Muse XHigh — Build → Luna — Review → Muse — Fix → Done
BUILDER STUCK      → DeepSeek V4 Flash → alternative → Muse retries
HARD PROBLEM       → GLM-5.3 → Muse implements
EXTREMELY HARD     → Kimi K3 → diagnosis/solution → Muse implements
HIGH-RISK CHANGE   → Qwen3.8 Max → independent review
```

---

## Core Principle

**Use intelligence where intelligence is required. Use cheap execution where execution is required.**

Default to Muse. Use Luna to think before large changes. Use DeepSeek for cheap alternatives. Use GLM when normal builders fail. Use Kimi only for genuinely difficult problems. Use Qwen as independent reviewer. Never spend frontier-model quota on work Muse can reliably perform.

---

## Mapping to this repository (Luna/Terra/Sol ↔ Go subscription)

- Repo `AGENTS.md` top section (Luna Max / Terra High/XHigh / Sol Ultra) remains the **legacy naming** for the same escalation ladder. In Go terms: Luna ≈ Planner (GPT-5.6 Luna High), Terra ≈ DeepSeek/GLM tier for bounded analysis, Sol ≈ Kimi/Qwen tier for architecture/security/release.
- For day-to-day work prefer the Go routing above; fall back to `Luna/Terra/Sol` labels only when referencing older docs or prompts that still use them.
