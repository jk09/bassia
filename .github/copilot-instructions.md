# MiniRepo development notes

- Target .NET 10 and keep nullable reference types enabled.
- Use Git through argument-safe process execution unless a feature requires a lower-level library.
- Keep the CLI small, cross-platform, and compatible with standard Git repositories.
- Follow the agentic workflow in `CLAUDE.md`: write a specification under `feat/` before non-trivial work, ask about open questions, and skip it for minor tasks pushed straight to `main`. Finish each spec-based run with an auto-merging pull request; notify the user only about failed checks or merge conflicts.
