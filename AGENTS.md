# Agent workflow

Use the following model roles for development tasks in this repository:

- The primary agent must use GPT-5.6 Sol with Ultra reasoning for requirements analysis, architecture, planning, coordination, code review, and final verification.
- Delegate implementation and other code-writing work to a sub-agent using GPT-5.6 Luna with Max reasoning.
- After the Luna agent finishes, the primary Sol agent must review the changes, run the appropriate build and tests, and fix or delegate any remaining issues before reporting completion.
- Keep architectural decisions and acceptance of the final result with the primary Sol agent. The Luna agent should follow the implementation task and constraints supplied by the primary agent.
- If the requested model or reasoning level is unavailable, report that limitation explicitly before using a different configuration.

This delegation rule applies whenever a task involves changing source code, build scripts, packaging, or tests. Read-only questions and repository inspection may be handled directly by the primary agent.
