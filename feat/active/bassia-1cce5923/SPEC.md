# Feedback and automatic error reports as GitHub issues

## Outcome

Anyone using `bassia` can send feedback with `bassia feedback send`, and Bassia can report its own crashes. Both create an
issue in the `jk09/bassia` GitHub repository. Nothing leaves the machine until the user has approved it: feedback is
shown and confirmed per submission, and crash reports are sent automatically only after the user has opted in once.

## Context

Results are TOML (or Markdown with `-human`) written by `ProgramCli.WriteResult`. `ProgramCli.RunAsync` reports the
failures in `IsReportable` as TOML errors; any other exception used to end the process with the .NET default output.
A crash can happen anywhere, also outside a monorepo, so the choice cannot live in a monorepo's `.bassia/config*.toml`;
it is kept per user next to the installer's folder (`BASSIA_HOME`). Bassia runs for agents and scripts as well as people,
so anything interactive is skipped when stdin, stdout or stderr is redirected (`ProgramCli.Interactive`).

## Acceptance criteria

- [x] `bassia feedback send [-kind bug|idea|question] [-yes] <message...>` shows the exact issue (repository, title,
      labels, body) and creates it only after the user confirms (`-yes` confirms in advance). Without confirmation, or
      without a terminal and without `-yes`, nothing is sent: exit code 0 with `sent = false`.
- [x] An unhandled exception in any command prints a short error with exit code 1 and builds a report (Bassia version,
      runtime, OS, command name, exception, redacted stack trace), handled by the user's `reports` choice: `off` does
      nothing; `ask` shows the report and asks; `auto` sends without asking. Not chosen yet behaves as `ask` at a terminal
      and as `off` without one.
- [x] The first interactive crash asks (yes / always / no / never); *always* and *never* are remembered per user. A
      non-interactive crash never asks and sends only under `auto`.
- [x] Reports are redacted before they are shown or sent: home folder, user and machine names, URL credentials and
      token-like strings. Arguments and prompts are never included, only the command name.
- [x] The same crash (exception type and innermost frames) is not filed twice from one machine; when an open issue
      already carries its fingerprint, the report is added to it as a comment.
- [x] A sending failure (no token, no network, API error) never changes the command's outcome; an approved report is kept
      and `bassia feedback pending -send` sends it later.
- [x] `docs/reference.md` and help describe the commands, what is collected and how to turn it off.
- [x] Unit tests cover redaction, fingerprints, the consent matrix (terminal × off/ask/auto/unset), the commands and the
      pending queue against a fake submitter; no test reaches GitHub.
- [ ] Verified once against the real repository with a real token (issue created, duplicate crash commented on).

## Approach

A `Feedback` area: `ReportBuilder` (builds and redacts the issue, fingerprints a crash), `Redactor`, `FeedbackStore`
(the user's choice, approved-but-unsent reports, filed fingerprints), `IIssueSubmitter` with `GitHubIssueSubmitter` (REST
API) and `CrashReporter` (the consent policy). `ProgramCli.RunAsync` has one catch-all around the command handler that
turns an unexpected exception into a crash report. `feedback send|reports|pending` are ordinary commands in
`CommandTable`, and `feedback` is never offered to or run by `bassia prompt`.

## Decisions

- **Authentication is a GitHub token** in `BASSIA_FEEDBACK_TOKEN` (else `GITHUB_TOKEN`) used with the REST API: no
  dependency on `gh`, no secret shipped in Bassia. Rejected: the `gh` CLI session, a relay service holding a bot token,
  and a pre-filled new-issue URL (not automatic).
- **Crash reports are off until chosen**: nothing is sent by default; the first interactive crash asks once and
  remembers *always*/*never*.
- **The choice is per user, not per monorepo**, stored in `feedback.toml` under `BASSIA_HOME` (the installer's folder),
  because a crash can happen outside a monorepo.
- Target repository is `jk09/bassia`; `feedback.toml` may name another with `repository` (forks).
- Only unhandled exceptions are reported automatically; ordinary reportable failures (bad input, git errors) are user
  errors and are not.

## Progress

- [x] Specification and open questions answered (token over REST; off until the first interactive crash asks).
- [x] Implementation: report building, redaction, consent policy, GitHub transport, commands.
- [x] Tests and documentation.
- [ ] Live check with a real token against `jk09/bassia`.

## Validation

`dotnet test` (new `FeedbackTests`); by hand `bassia feedback send -yes test` and a forced crash with `BASSIA_FEEDBACK_TOKEN`
set, in each of the `off`, `ask` and `auto` modes.
