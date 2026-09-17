# `xam` CLI and the initial setup

## Outcome

Initialize a `Bassia` monorepo and add components into it.


## Context

I want to initialize an empty `Bassia` repo, in, say, an empty volume `R:\`.

```sh
R:\> bassia setup init
R:\> bassia setup add-component https://github.com/myrepo/component_1.git
R:\> bassia setup add-component https://github.com/myrepo/component_2.git
R:\> # populate components
```
creates the following folder structure

```
R:\
|__ .bassia                   <-- meta-repo
    |__ .git                  <-- meta-repo storage. Prospective alternative: distributed database
    |__ config.toml         
    |__ components.toml       <-- monorepo components registration
|__ component_1
    |__ .git                  <-- cloned on-demand as a bare repo, maybe sparsely, when a project is worked on
|__ component_2
    |__ .git
|__ .workspace                <-- workspace for agents, `git`  worktrees of components
    |__ ...

```

- `bassia setup init` called in an empty folder creates the `.bassia` meta-repo folder. 
- Preferable location of a `Bassia` monorepo is an empty Windows *Dev Drive volume* for better performance and isolation.

## Acceptance criteria

- [ ] Able to initialize an empty `Bassia` monorepo in an empty folder or volume
- [ ] Able to clone repos (e.g. from `GitHub`) and add them as `Bassia`  monorepo components. Bare repos are cloned.
- [ ] On success or failure AI-parsable messages are printed (e.g. in JSON format)

## Approach

Use standard approach for command line utilities parameter parsing. Avoid 3rd party libraries for this functionality.

## Decisions

- `bassia setup` provides initialization of a `Bassia` monorepo, and adding components into it.

## Progress

- [ ] `bassia setup` with basic commands

## Validation

Validate commands
- `bassia setup init`
- `bassia add-component https://github.com/jk09/example.git`