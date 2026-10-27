#!/usr/bin/env bash
# Starts one agentic run on an agent worker: an ECS Fargate task of the worker image in the server's region, which
# logs in with a worker token for the run's components, clones the server lazily and runs `bassia agent start -spec`
# (deploy/docker/worker-entrypoint.sh).
#
#   scripts/run-agent.sh -env bassia -components app,lib -spec run.spec -prompt "fix the parser"
#   scripts/run-agent.sh -env bassia -components app -spec run.spec -run "make fix" -minutes 60
#
# Needs: the AWS CLI with credentials for the account, jq, and bassia logged in to the server (bassia login), whose
# user must hold 'run' on the components: the worker's token is made from that login and holds no more than it.
# The token reaches the task as an environment override, visible to whoever may describe the task; it expires after
# -minutes (default 240).
set -euo pipefail

env_name=""; components=""; spec=""; prompt=""; run=""; minutes=240
while [ $# -gt 0 ]; do
	case "$1" in
		-env) env_name="$2"; shift 2 ;;
		-components) components="$2"; shift 2 ;;
		-spec) spec="$2"; shift 2 ;;
		-prompt) prompt="$2"; shift 2 ;;
		-run) run="$2"; shift 2 ;;
		-minutes) minutes="$2"; shift 2 ;;
		*) echo "unknown option $1" >&2; exit 2 ;;
	esac
done
[ -n "$env_name" ] && [ -n "$components" ] && [ -f "$spec" ] && { [ -n "$prompt" ] || [ -n "$run" ]; } || {
	echo "usage: $0 -env <environment> -components <name,...> -spec <file> (-prompt <text> | -run <command>) [-minutes <n>]" >&2
	exit 2
}

output() { aws cloudformation describe-stacks --stack-name "$1" --query "Stacks[0].Outputs[?OutputKey=='$2'].OutputValue" --output text; }
server_url=$(output "$env_name-server" Url)
cluster=$(output "$env_name-server" ClusterName)
task_definition=$(output "$env_name-workers" WorkerTaskDefinition)
subnets=$(output "$env_name-workers" WorkerSubnets)
security_group=$(output "$env_name-workers" WorkerSecurityGroup)
capacity_provider=$(output "$env_name-workers" WorkerCapacityProvider)

token=$(bassia token create -server "$server_url" -components "$components" -minutes "$minutes" | sed -n 's/^token = "\(.*\)"$/\1/p')
[ -n "$token" ] || { echo "could not make a worker token: is bassia logged in to $server_url?" >&2; exit 1; }

overrides=$(jq -n --arg token "$token" --arg spec "$(cat "$spec")" --arg prompt "$prompt" --arg run "$run" '{
	containerOverrides: [{
		name: "worker",
		environment: ([
			{ name: "BASSIA_TOKEN", value: $token },
			{ name: "BASSIA_SPEC", value: $spec }
		] + (if $prompt != "" then [{ name: "BASSIA_PROMPT", value: $prompt }] else [{ name: "BASSIA_RUN", value: $run }] end))
	}]
}')

aws ecs run-task \
	--cluster "$cluster" \
	--task-definition "$task_definition" \
	--capacity-provider-strategy "capacityProvider=$capacity_provider,weight=1" \
	--network-configuration "awsvpcConfiguration={subnets=[$subnets],securityGroups=[$security_group],assignPublicIp=DISABLED}" \
	--overrides "$overrides" \
	--query 'tasks[0].taskArn' --output text
