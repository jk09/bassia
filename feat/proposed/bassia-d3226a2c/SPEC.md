# `xam` CLI and the initial setup

## Outcome

The `bassia setup` command will initialize a `Bassia` repo and registers existing `git` repositories/components with it.


## Context

I want to initialize an empty `Bassia` repo, in, say, an empty volume `R:\`.

```sh
R:\> bassia setup
```
creates a folder structure

```
R:\
|__ .bassia                   <-- meta-repo
    |__ .git                  <-- meta-repo storage. Prospective alternative: distributed database
    |__ config.toml         
    |__ components.toml       <-- monorepo components (= `git` repositories)
|__ component_1
    |__ .git
    |__ ...       
|__ component_2
    |__ .git
    |__ ...

```
Explain the problem, relevant constraints, and links to dependent feature records or external issues.

## Acceptance criteria

- [ ] State observable behavior that can be verified.
- [ ] Include failure behavior and compatibility expectations where relevant.

## Approach

Describe the intended boundaries and major implementation steps. Avoid file-by-file instructions that will become stale.

## Decisions

- Record durable decisions and their rationale as they are made.

## Progress

- [ ] Add only meaningful implementation and validation milestones.

## Validation

List the commands or manual checks that prove the acceptance criteria.