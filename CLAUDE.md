# `XAM` - agentic monorepo

`XAM` is my own version control implementation suited for 1. agentic development, 2. scalable monorepo support, 3. `git` compatibility.

## Architecture

`XAM` is a collection of individual `git` repos, acting as components (in the spirit of [BitKeeper](https://www.bitkeeper.org/) components - "submodules done right"). Each component encapsulates an architecturally distinct part: library, frontend, backend, project, executable application etc. The components are registered with the meta-repo, which acts as the front and root of the `XAM` monorepo. The meta-repo maintains metadata, including *component dependencies*. Component dependencies allow to transparently limit the amount of repos which need to be cloned (in addition to the meta-repo) when working on a particular project. In other words, a component is an unit of scalability, modularity, and also security delineation.

## Tech stack

The `XAM` initially consists of a command-line frontend written in .NET which relies on a separately installed `git` to maintain the components and meta-repo. 
