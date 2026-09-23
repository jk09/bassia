# Responsive interactive frontend: background runs and wallboards

## Outcome

`bassia ui` becomes a live, key-driven cockpit instead of a modal menu tree. An agentic run starts in the
background and the prompt comes back immediately; the running runs animate on a Jenkins-style wallboard of
rectangles and can be cancelled from it. Two top-level views - **Components** and **Agentic runs** - are one
keystroke apart, and the components view draws the registered components as rectangles connected by ASCII
dependency lines. Everything the menu-driven frontend could do is still reachable.

## Context

The first frontend (`feat/done/bassia-a7b47671`) was a stack of `SelectionPrompt` menus. Every action blocked the
session: *Start an agentic run* handed its console to the agent process and the user waited for it, so two runs
could only be started from two terminals even though `bassia agent` itself is built for parallel runs.
`Stop` / `Suspend` / `Resume` / `Hand off` / `Integrate results` were listed as stubs.

Constraints:

- `bassia agent` stays the single implementation of a run; the frontend must not fork its own copy of the
  materialize/commit/tag/push sequence.
- Scriptable CLI behavior (stderr progress lines, TOML results, exit codes) must not change.
- The agent process currently inherits stdout; a background run cannot, or it would overwrite the wallboard.

## Acceptance criteria

- [x] Starting a run from the frontend returns to the board at once; the run appears as a live card and the
      frontend keeps accepting keys, including starting further runs in parallel.
- [x] A live card animates and shows phase, elapsed time, components, the command and the agent's latest output line.
- [x] A live run can be cancelled from the frontend: the agent process tree is killed, the run record is committed
      with status `cancelled`, and nothing is committed into the components.
- [x] `1` and `2` switch directly between the components and the runs view from anywhere in the frontend.
- [x] The runs view is a wallboard of rectangles, one per run, wrapped to the terminal width.
- [x] The components view is a wallboard of rectangles connected by ASCII lines that follow `references`.
- [x] Each rectangle carries its own basic facts (components: tags, branch, references, referrers, activity;
      runs: id, status, elapsed, components, command, latest output).
- [x] Everything the menu frontend offered is still reachable: component detail with refs/tree/runs, annotated
      tagging, the dependency tree with Mermaid/SVG export, the recorded run list and run detail, the start-a-run
      wizard, and the remaining lifecycle stubs.
- [x] `bassia agent` from the command line keeps its TOML results, exit codes and `bassia: ...` stderr progress
      lines; the only change is one added progress line where the commit/tag/push sequence starts.

## Approach

1. Give `AgentCommand.StartRunAsync` an optional `AgentRunContext`: a cancellation token, a step callback
   (replacing the `bassia: ...` stderr lines when present) and an output callback (which switches the agent
   process from inheriting the console to redirected pipes). No context means today's CLI behavior.
2. Add the `cancelled` run status: the agent process is killed as a tree, the record is committed, the run folder
   is kept for inspection, and `agent retry` refuses it like `started`/`failed`.
3. `RunSupervisor` owns the background tasks and exposes immutable `RunCard` snapshots to the renderer.
4. `CharCanvas` draws boxes and merges box-drawing lines by edge mask, so crossings become `┬`/`┴`/`┼`.
   `ComponentBoard` and `RunBoard` lay the cards out on it.
5. `InteractiveSession` becomes a render/key loop over a view state, repainting on a tick only while something is
   live. Wizards and prompts stay Spectre prompts, so the existing flows are unchanged.

## Decisions

- **Keys, not menus, for navigation; prompts stay for data entry.** A wizard is a sequence of questions and works
  well as a prompt; navigation must not block, so it is a key loop. Rejected: a full re-render with in-house text
  entry - it would have re-implemented `MultiSelectionPrompt` for no gain.
- **Both wallboards are drawn on one character canvas**, not with `Grid`/`Panel`. The components board needs
  edges *between* cells, which a table layout cannot draw, and sharing the canvas keeps the two views visually one
  system. The canvas emits Spectre markup, so colors and `TestConsole` assertions still work.
- **Idle repaints only while a run is live.** A 4 Hz full-screen repaint of a static board is visible flicker for
  no information.
- **Cancellation stops at the agent process.** Once the agent has exited and the commit/tag/push sequence has
  begun, a cancel would leave a component half-finalized; the sequence is short and idempotent, so it is allowed
  to finish and the run ends `completed`/`partial` as usual.
- **Quitting with live runs asks first.** The runs are in-process tasks, so leaving would orphan the agent
  processes and lose the record update; the frontend offers to cancel them and waits.

## Progress

- [x] `AgentRunContext`, `cancelled` status, output capture and cancellation in `AgentCommand`.
- [x] `RunSupervisor` with `RunCard` snapshots.
- [x] `CharCanvas` with edge-mask line merging.
- [x] `ComponentBoard` and `RunBoard`.
- [x] `InteractiveSession` rewritten as a view/key loop; all former actions kept.
- [x] Tests for the canvas, both boards, the supervisor, cancellation, and the frontend flows.
- [x] README updated.

## Validation

```powershell
dotnet build
dotnet test --filter Category!=EndToEnd
```
