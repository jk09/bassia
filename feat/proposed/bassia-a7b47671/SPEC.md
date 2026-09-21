# Command line frontend for monorepo management

## Outcome

The user will have access to a command line frontend which will enable them, in an user friendly way, to manage frequent activities such as component dependency observation, agentic runs monitoring, merging the results of agentic runs, etc.

## Context

<!-- Explain the problem, relevant constraints, and links to dependent feature records or external issues. -->

The monorepo consists of potentially many components with complex dependencies expressed by a directed acyclic graph.  Each component contains many tags representing baseline state as well as the outcomes of agentic runs. We need an user-friendly frontend (user being an human here) to monitor the components and agentic runs.

We need an interactive command line frontend tool which will be able to display and interact with the list of components, agentic runs, and  associated actions in the console, using ASCII art graphics, selectors etc., in the spirit of Claude or Copilot command line tools.
The tool should be started and provide intuitive choice selectors for different actions, such as:

### Component display

The frontend displays the of components as  a detailed list, or a directed acyclic graph. An export at least one of SVG and Markdown Mermaid should be possible in order to display the graph in a  Web browser or a Markdown viewer.

The `git` tree of each component should be displayed, along with references such as branches and tags.
Especially the list of annotated tags for each component should be accessible, to be used in the agentic runs. The user should be able to create a new annotated tag using existing references.

### Agentic run startup

The user should be able to interactively select one or more components and their  tags, and provide an agentic prompt (along with parameters such as the AI model, context, effort etc.), whereupon the agentic run commences using `Bassia` infrastructure (the command `bassia agent  -select ... -run ...`). The information about agentic run will be accessible in the frontend, in a manner similar to command line agentic tools such as Claude. The user will be able to stop, suspend, resume, or handoff the agentic run (non-basic functionality can be stubbed for now). The list of all running and completed agentic runs will be available, along with generated tags in respective components. Prospectively, we want to be able to interpret the monorepo in terms of agentic runs replacing the role of `git` branches in the sense of each agentic run being a flow  branching off a certain component tag, proceeding for few steps (due to user interactive input to the agent), and possibly being integrated with outcomes of other agentic run. The integration is an analogue of `git` merge and will be the subject of future work, using AI-assisted merging taking into consideration not only `git`  diffs, but also the semantics of the agentic run outcomes. Each agentic run can be represented as a flow by virtue of annotated tag with counters implemented earlier, which are present in the top-level component repos. The frontend tool will provide not only the list of agentic runs, and their relation to components, but also the reverse information: how any components has been affected by any agentic run.

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