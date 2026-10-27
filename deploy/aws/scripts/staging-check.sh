#!/usr/bin/env bash
# The two-client scenario of bassia-f245cd5f against a deployed environment (the staging workflow runs it): an admin
# and a second user, each with a clone, see each other's runs, records and holds, and landing on main takes
# 'integrate'. Prints one line per check and exits non-zero when one fails.
#
#   scripts/staging-check.sh -env bassia-staging
#
# Needs the AWS CLI, jq, git and bassia on PATH; the environment's configuration names the admin (admins[0]).
set -euo pipefail

[ "${1:-}" = "-env" ] && [ -n "${2:-}" ] || { echo "usage: $0 -env <environment>" >&2; exit 2; }
env_name="$2"
here="$(cd "$(dirname "$0")" && pwd)"
admin=$(jq -r '.admins[0]' "$here/../config/${env_name#bassia-}.json" 2>/dev/null || echo admin)
url=$(aws cloudformation describe-stacks --stack-name "$env_name-server" --query "Stacks[0].Outputs[?OutputKey=='Url'].OutputValue" --output text)
work=$(mktemp -d)
export BASSIA_CREDENTIAL_STORE=file BASSIA_CREDENTIALS_DIR="$work/credentials"
failures=0
check() { if "${@:2}" > "$work/last.log" 2>&1; then echo "ok   $1"; else echo "FAIL $1"; sed 's/^/     /' "$work/last.log"; failures=$((failures + 1)); fi; }
token_for() { "$here/server-task.sh" -env "$env_name" token -user "$1" -minutes 60 | tail -n 1; }
as() { local token="$1"; shift; BASSIA_TOKEN="$token" "$@"; }

echo "Checking $url"
check "the server answers /healthz and /readyz" sh -c "curl -fsS '$url/healthz' && curl -fsS '$url/readyz'"
admin_token=$(token_for "$admin")

# The admin registers the users and a component; bob may run on everything.
as "$admin_token" bassia clone -from "$url" -path "$work/admin" > /dev/null
cd "$work/admin"
check "the admin registers themself" as "$admin_token" bassia user add -id "$admin" -name Admin -email admin@example.invalid
check "the admin registers bob" as "$admin_token" bassia user add -id bob -name Bob -email bob@example.invalid
check "the admin turns locking on" as "$admin_token" bassia config set -key lock.files -value "**"
check "the admin creates a component" as "$admin_token" bassia component create -name staging-lib
check "the admin grants bob run" as "$admin_token" bassia access grant -to bob -permission run
bob_token=$(token_for bob)
as "$bob_token" bassia clone -from "$url" -path "$work/bob" > /dev/null

# A run in the admin's clone, seen from bob's.
run=$(as "$admin_token" bassia agent start -select staging-lib -run "echo staged > staging-lib/staged.txt" | sed -n 's/^run_id = "\(.*\)"$/\1/p')
check "a run in one clone records its result" test -n "$run"
cd "$work/bob"
check "the other clone lists the run" sh -c "BASSIA_TOKEN='$bob_token' bassia agent list | grep -q '$run'"
check "the other clone shows the run's released holds" sh -c "BASSIA_TOKEN='$bob_token' bassia lock list | grep -q 'staged.txt'"

# An integration from bob's clone; landing it takes integrate, which the admin has.
integration=$(as "$bob_token" bassia integration start -runs "$run" | sed -n 's/^integration_id = "\(.*\)"$/\1/p')
check "the other clone integrates the run" test -n "$integration"
check "landing is refused without integrate" sh -c "! BASSIA_TOKEN='$bob_token' bassia integration advance '$integration'"
cd "$work/admin"
check "the admin lands the integration" as "$admin_token" bassia integration advance "$integration"
check "main has the run's file, as the server has it" sh -c "git -C '$work/admin/staging-lib' ls-tree -r --name-only main | grep -q staged.txt"

rm -rf "$work"
echo "$failures check(s) failed"
[ "$failures" -eq 0 ]
