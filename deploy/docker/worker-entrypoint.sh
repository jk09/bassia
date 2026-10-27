#!/bin/sh
# One agentic run on a Bassia server, as an ECS task runs it (deploy/aws/scripts/run-agent.sh starts it):
#   BASSIA_SERVER   the server's URL
#   BASSIA_TOKEN    a worker token for the run's components (bassia token create), 'run' at most
#   BASSIA_SPEC     the run's config spec (map, select, load lines)
#   BASSIA_PROMPT   the agent's prompt (bassia agent start -prompt), or
#   BASSIA_RUN      the agent command (bassia agent start -run)
# The clone is lazy: only the components the spec maps are imported, and only the files it loads are downloaded.
set -eu
: "${BASSIA_SERVER:?BASSIA_SERVER must be set}" "${BASSIA_TOKEN:?BASSIA_TOKEN must be set}" "${BASSIA_SPEC:?BASSIA_SPEC must be set}"

bassia clone -from "$BASSIA_SERVER" -lazy -path /work/monorepo
printf '%s\n' "$BASSIA_SPEC" > /work/run.spec
cd /work/monorepo
if [ -n "${BASSIA_PROMPT:-}" ]; then
	exec bassia agent start -spec /work/run.spec -prompt "$BASSIA_PROMPT"
else
	exec bassia agent start -spec /work/run.spec -run "${BASSIA_RUN:?BASSIA_PROMPT or BASSIA_RUN must be set}"
fi
