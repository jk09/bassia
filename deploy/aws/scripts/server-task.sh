#!/usr/bin/env bash
# Runs `bassia-server <command...>` once, as an ECS task of the server image in the environment's subnets, waits for it
# and prints what it wrote. For the jobs an operator does next to the service:
#
#   scripts/server-task.sh -env bassia token -user admin -minutes 60    a token for an admin or registered user
#   scripts/server-task.sh -env bassia check                             S3 versioning and DynamoDB recovery are on
#   scripts/server-task.sh -env bassia gc                                collect the histories' garbage now
#
# Environment variables of the form OVERRIDE_<NAME>=<value> replace the task's BASSIA_<NAME> (the restore drill points a
# task at restored resources that way). Needs the AWS CLI and jq.
set -euo pipefail

[ "${1:-}" = "-env" ] && [ -n "${2:-}" ] && [ -n "${3:-}" ] || { echo "usage: $0 -env <environment> <command> [options...]" >&2; exit 2; }
env_name="$2"; shift 2

output() { aws cloudformation describe-stacks --stack-name "$env_name-server" --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue" --output text; }
cluster=$(output ClusterName)
task_definition=$(output TaskDefinition)
subnets=$(output TaskSubnets)
security_group=$(output TaskSecurityGroup)
log_group=$(output LogGroup)

environment=$(env | sed -n 's/^OVERRIDE_\([A-Z_]*\)=\(.*\)$/\1\t\2/p' | jq -R -s 'split("\n") | map(select(length > 0) | split("\t") | { name: ("BASSIA_" + .[0]), value: .[1] })')
overrides=$(jq -n --argjson command "$(printf '%s\n' "$@" | jq -R . | jq -s .)" --argjson environment "$environment" \
	'{ containerOverrides: [{ name: "gc", command: $command, environment: $environment }] }')

task=$(aws ecs run-task --cluster "$cluster" --task-definition "$task_definition" --launch-type FARGATE \
	--network-configuration "awsvpcConfiguration={subnets=[$subnets],securityGroups=[$security_group],assignPublicIp=DISABLED}" \
	--overrides "$overrides" --query 'tasks[0].taskArn' --output text)
aws ecs wait tasks-stopped --cluster "$cluster" --tasks "$task"

exit_code=$(aws ecs describe-tasks --cluster "$cluster" --tasks "$task" --query 'tasks[0].containers[0].exitCode' --output text)
aws logs get-log-events --log-group-name "$log_group" --log-stream-name "gc/gc/${task##*/}" --start-from-head --query 'events[].message' --output text | tr '\t' '\n'
[ "$exit_code" = "0" ] || { echo "the task ended with $exit_code" >&2; exit 1; }
