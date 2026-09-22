# TOML command results

Status: done

## Outcome

Every `bassia` command prints its machine-readable result as TOML instead of JSON: on stdout when it succeeded, on stderr when it failed, opened by the comment line `# bassia result`. Results now use the same notation and the same `snake_case` vocabulary as everything else `Bassia` stores, so an agent or a script reads a result and a `run.toml` record with one parser.

## Context

Results were emitted as indented JSON so an agent could consume them without scraping prose. Nothing else in `Bassia` is JSON: `config.toml`, `components.toml`, the run records in `.agentic-runs`, and the TOML body of every result commit are all TOML, and they name the same things differently — a run record calls it `run_id` and `result_status`, a result called it `runId` and `resultStatus`. A caller that reads both needed two parsers and two spellings of the same fields.

TOML keeps the properties that made JSON worth printing (unambiguous structure, quoting and escaping handled by a parser rather than by the reader) and adds one: `agent` results and the run records they summarize become the same document shape.

## Acceptance criteria

- [x] Every command's result is a TOML document; none of the CLI's machine-readable output is JSON.
- [x] A result opens with the comment line `# bassia result`, so a caller can find where it starts in output that `bassia agent` shares with the agent command's own stdout.
- [x] The document starts with `ok`, `command`, and `message` (success) or `error` (failure); per-command keys follow, and `agent` adds one `[[component]]` section per component.
- [x] Result keys are `snake_case` and match the corresponding `run.toml` keys (`run_id`, `result_status`, `result_tag`, `[[component]]`).
- [x] Values TOML cannot express are absent keys rather than `null` (for example a component with no `result_error`), and a multi-line message such as a usage text stays a single value.
- [x] The result still goes to stdout on success and stderr on failure, with the exit codes unchanged.
- [x] The hand-check scripts in `scripts/` read the TOML result and keep working.

## Approach

- **One writer**: `ProgramCli.WriteResult` keeps its signature (ok, command, message, optional data) and hands the payload to a new `TomlResult`, which maps the payload onto Tomlyn's model and serializes it. Commands are unchanged apart from their key spellings; they never format TOML themselves.
- **Ordering**: a TOML table's own key/value pairs must all precede its first sub-table, so `TomlResult` writes scalars before sections regardless of the order a command filled its dictionary in. This is the one rule a command would otherwise have to remember.
- **Scripts**: PowerShell has no TOML reader, and the printed subset is small and machine-generated, so `scripts/Bassia.common.ps1` parses it directly: scalars, inline arrays of strings, and `[[section]]` blocks collected into an array of objects.

## Decisions

- **A comment line as the marker, rather than wrapping the result in a `[result]` table.** `bassia agent` lets the agent command inherit stdout, so a result can be preceded by arbitrary output and a caller needs an unambiguous starting point — JSON's was a line holding just `{`. Wrapping everything in `[result]` would give one too, but it would also push every key one level down and turn `[[component]]` into `[[result.component]]`. A comment is invisible to a parser, so the marked text stays a valid document on its own.
- **`snake_case` keys, matching `run.toml`.** The alternative, keeping the JSON spellings, would have made the change purely syntactic and left the two vocabularies in place.
- **The components array is `[[component]]`, not `[[components]]`,** for the same reason: it is the key `run.toml` and the result commit bodies already use.
- **Windows paths keep TOML's basic-string escaping (`C:\\temp`) rather than being emitted as literal strings.** Literal strings would read better, but the stored records escape them the same way, and matching those matters more than the backslashes.

## Progress

- [x] `TomlResult` serializes a result payload; `ProgramCli.WriteResult` uses it and the JSON serializer is gone.
- [x] `init`, `add-component` and `agent` (start, retry, abandon) emit `snake_case` keys and `[[component]]`.
- [x] `scripts/Bassia.common.ps1` reads the TOML result; `Invoke-AgentRun.ps1` follows the renamed fields.
- [x] Tests cover the format itself (`TomlResultTests`) and assert on TOML in the command tests.
- [x] README documents the result format.

## Validation

- `dotnet test` — the whole suite, including `TomlResultTests` (marker, scalar-before-section ordering, dropped nulls, escaped multi-line messages) and the command tests asserting `ok = true`, `status = "completed"`, `result_status = "pushed"`.
- Hand-check with the scripts against a throwaway monorepo: `init`, `add-component` and a full `agent -select ... -run ...` through `Invoke-Bassia`, confirming the returned object exposes `run_id`, `status`, `metadata_tags` and `component`, and that the unescaped `workspace` path exists on disk.
