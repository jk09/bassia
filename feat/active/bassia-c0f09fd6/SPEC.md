# Load rules: sparse workspaces that fetch, cache and hold only part of the monorepo

## Outcome

A workspace's folder holds only the paths its load rules select. These work like the load rules of a ClearCase
snapshot view's config spec. A clone fetches and caches the content of those paths only, and never reads the trees of
directories it does not load. Many clients can then run agents on their own parts of one server's monorepo, each
downloading only what its work needs. The workspace's tree still holds everything: snapshots never delete what is not
loaded, and sync and landing carry it along unchanged.

## Context

- Before this, every workspace was a full checkout. A clone downloaded the content of every file of mainline for each
  new checkout, though only once per clone thanks to the shared cache. For a large monorepo with many clients, each
  working on one project, most of that is waste.
- The user asked for a test with Azurite and PostgreSQL in containers, a bassia-server, and agents on two clients that
  cache what they need, build and test, and land seq after seq. The user also asked for a config spec for what a
  client caches up front.
- The user reported that a landing found "no changes against its base" after an agent "succeeded". The cause was an
  empty monorepo (`main_seq = 0`), not landing: the agent's workspace held no files.

## Acceptance criteria

- [x] `LoadRules`: rules are paths from the root, tried in order, with the last match deciding.
  - Literal paths name a file or a directory with its subtree; a trailing `/` matches directories only.
  - Globs (`*`, `?`, `**`) match files at their depth; `!` excludes.
  - `all`, or no rules, loads everything; `.bassia.toml` is always loaded.
  - `MayLoadUnder(dir)` prunes directories nothing could match in.
- [x] A sparse checkout writes only loaded files and shows unloaded directories as empty folders. It never reads an
      object (tree or content) below a pruned directory.
- [x] Snapshots never read unloaded paths as deleted, and a file an agent creates in an unloaded folder is added.
      Landing a sparse workspace leaves every unloaded path of mainline as it was.
- [x] Sync updates loaded files and leaves unloaded ones on the server.
- [x] `workspace load` changes the rules: it snapshots first, so edits to dropped paths stay on the workspace, then
      removes dropped paths and writes added ones. Without `-load` it shows the rules.
- [x] Rules come from `-load` on `workspace create|clone|checkout`, `agent start|replay` and `workspace load`. Without
      `-load`, the order is: this machine's `workspace.load`, then mainline's `.bassia.toml` `[load] default`, then
      everything. `@name` expands to a profile of `[load.profiles]`, and a replay loads what its original loaded. An
      unknown profile fails with the list of profiles.
- [x] Results report `load` and `files_loaded`; `docs/reference.md` documents load rules, the `.bassia.toml` `[load]`
      table and `workspace.load`.
- [x] A live test on Azurite + PostgreSQL with a bassia-server passes. Two clients with different profiles run Claude
      Code agents concurrently, each building and testing its project. They land at seq 4 and 5 after two imports
      (seq 1, 2) and the policy (seq 3). Each client caches only its project, and an excluded 600 KiB fixture is never
      downloaded. A full checkout of seq 5 passes both projects' tests.

## Approach

`Bassia.Core/Workspaces/LoadRules.cs` holds the rules. The tree state stores them and tracks only loaded paths.
`CheckoutProvider.UpdateAsync` diffs with a directory filter (`Trees.DiffAsync(..., descend)`): a pruned directory comes
back as one entry instead of being read. Prefetch and writes cover loaded files only. Deletions apply to tracked paths
only, so unloaded paths can never turn into deletions. `IWorkspaceProvider.LoadAsync` changes the rules of an existing
folder. `RepoPolicy` reads `[load]`, and `Monorepo.ResolveLoadAsync` applies the precedence above.

## Decisions

- Rules are per workspace folder, on the client (in its tree state), not on the server: two machines can load the
  same workspace differently, and a load is a property of a checkout, as in ClearCase snapshot views.
- What to cache up front is what is loaded, the whole of it, fetched in batches before files are written. Lazy
  fetch-on-open (ProjFS) stays out of scope. An agent that needs more runs `bassia workspace load`.
- Unloaded directories show as empty folders, so an agent sees the monorepo's shape (for example "list every
  top-level project") without downloading it. Unloaded files at loaded levels are not shown.
- Last match wins (gitignore-like), so an exclusion can carve a hole into a loaded directory (`api/`,
  `!api/testdata/`), and a later include can reopen it.
- `.bassia.toml` is always loaded: a snapshot needs its ignore rules.
- Found in the live test: in `agent.command`, Claude Code's `--allowedTools` takes a list and swallows the appended
  prompt. The docs now say to keep `-p` last.

## Progress

- [x] Core, provider, policy, CLI, config.
- [x] Tests: 38 load-rule unit and workspace tests, a CLI test, the full suite (196).
- [x] Live two-client test with Claude Code agents on Azurite + PostgreSQL.
- [ ] CI green on the PR.

## Validation

- `dotnet test` (LoadRulesTests, CliTests.LoadRules_*).
- The live test: `bassia-local-cloud.ps1 up`, `serve`, `import`, then a policy landing, two clones with
  `agent start -load @api|@web -land`, cache inspection by object id, and a full checkout running both projects' tests.
