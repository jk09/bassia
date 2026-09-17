# Feature records

The `feat/` directory is the versioned source of truth for work that spans more than one change or agent session. A feature record preserves intent, constraints, decisions, and progress beside the code it affects.

## Workflow

1. Create `feat/<short-name>.md` from `feat/template.md` before implementation.
2. Keep the record focused on observable behavior and durable decisions. Do not use it as a transcript or scratchpad.
3. Update its status and checklist in the same commits that change the implementation.
4. Record material design changes under **Decisions**, including rejected alternatives when they may be reconsidered later.
5. Set the status to `done` when the acceptance criteria are verified. Keep completed records in Git history; delete them in the completing change unless they remain useful as product documentation.

Use one record per independently deliverable feature. Split a record when parts can ship separately; link records when one depends on another.

## Status values

- `proposed`: intent is captured but implementation has not started.
- `active`: implementation or validation is in progress.
- `blocked`: progress requires a named decision or dependency.
- `done`: all acceptance criteria have been verified.

The feature record explains *why* and *what*. Code, tests, and commits remain the authority for *how*.