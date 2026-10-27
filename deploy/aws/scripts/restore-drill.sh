#!/usr/bin/env bash
# The restore drill (docs/deploy-aws.md, "Restore"): restores the environment's database, table and bucket to one moment
# into new resources beside the live ones, checks that a server task works on them (migrations run, the cloud storage's
# check passes, garbage collection reads every history), prints how long it took, and - with -cleanup - removes what it
# restored. The live environment is not touched.
#
#   scripts/restore-drill.sh -env bassia-staging [-at 2026-10-27T08:00:00Z] [-cleanup]
#
# Needs the AWS CLI and jq. Restoring the bucket copies the version of every object that was current at the moment;
# a large bucket takes a while (S3 Batch Operations does it faster, see the docs).
set -euo pipefail

env_name=""; at=""; cleanup=false
while [ $# -gt 0 ]; do
	case "$1" in
		-env) env_name="$2"; shift 2 ;;
		-at) at="$2"; shift 2 ;;
		-cleanup) cleanup=true; shift ;;
		*) echo "unknown option $1" >&2; exit 2 ;;
	esac
done
[ -n "$env_name" ] || { echo "usage: $0 -env <environment> [-at <ISO time>] [-cleanup]" >&2; exit 2; }
here="$(cd "$(dirname "$0")" && pwd)"
at="${at:-$(date -u -d '-10 minutes' +%Y-%m-%dT%H:%M:%SZ)}"
drill="drill-$(date -u +%m%d%H%M)"
started=$(date +%s)
output() { aws cloudformation describe-stacks --stack-name "$1" --query "Stacks[0].Outputs[?OutputKey=='$2'].OutputValue" --output text; }

bucket=$(output "$env_name-data" BucketName)
table=$(output "$env_name-data" TableName)
cluster=$(aws rds describe-db-clusters --query "DBClusters[?TagList[?Key=='aws:cloudformation:stack-name' && Value=='$env_name-data']].DBClusterIdentifier | [0]" --output text)
security_group=$(aws rds describe-db-clusters --db-cluster-identifier "$cluster" --query 'DBClusters[0].VpcSecurityGroups[0].VpcSecurityGroupId' --output text)
subnet_group=$(aws rds describe-db-clusters --db-cluster-identifier "$cluster" --query 'DBClusters[0].DBSubnetGroup' --output text)
echo "Restoring $env_name as of $at into $drill-*"

# The database and the table, to the same moment, in parallel.
aws rds restore-db-cluster-to-point-in-time --source-db-cluster-identifier "$cluster" --db-cluster-identifier "$cluster-$drill" \
	--restore-to-time "$at" --db-subnet-group-name "$subnet_group" --vpc-security-group-ids "$security_group" \
	--serverless-v2-scaling-configuration MinCapacity=0.5,MaxCapacity=4 > /dev/null
aws rds create-db-instance --db-cluster-identifier "$cluster-$drill" --db-instance-identifier "$cluster-$drill-writer" \
	--engine aurora-postgresql --db-instance-class db.serverless > /dev/null
aws dynamodb restore-table-to-point-in-time --source-table-name "$table" --target-table-name "$table-$drill" --restore-date-time "$at" > /dev/null

# The bucket: every key's version that was current at the moment, into a new bucket.
target="$bucket-$drill"
aws s3api create-bucket --bucket "$target" --create-bucket-configuration "LocationConstraint=$(aws configure get region || echo "$AWS_REGION")" > /dev/null
aws s3api put-bucket-versioning --bucket "$target" --versioning-configuration Status=Enabled
aws s3api list-object-versions --bucket "$bucket" --output json \
	| jq -r --arg at "$at" '[.Versions[]? | select(.LastModified <= $at)] | group_by(.Key) | map(max_by(.LastModified)) | .[] | [.Key, .VersionId] | @tsv' \
	| while IFS=$'\t' read -r key version; do
		aws s3api copy-object --bucket "$target" --key "$key" --copy-source "$bucket/$key?versionId=$version" > /dev/null
	done

aws rds wait db-instance-available --db-instance-identifier "$cluster-$drill-writer"
aws dynamodb wait table-exists --table-name "$table-$drill"
host=$(aws rds describe-db-clusters --db-cluster-identifier "$cluster-$drill" --query 'DBClusters[0].Endpoint' --output text)
restored=$(date +%s)
echo "Restored in $(( (restored - started) / 60 )) minutes; checking a server on it"

# A server task on the restored set: migrations, the storage check, and a collection that reads every history.
export OVERRIDE_DB_HOST="$host" OVERRIDE_TABLE="$table-$drill" OVERRIDE_BUCKET="$target"
"$here/server-task.sh" -env "$env_name" migrate
"$here/server-task.sh" -env "$env_name" gc -grace-days 3650
checked=$(date +%s)
echo "Recovery time: $(( (checked - started) / 60 )) minutes (restore $(( (restored - started) / 60 )), check $(( (checked - restored) / 60 )))."

if $cleanup; then
	aws rds delete-db-instance --db-instance-identifier "$cluster-$drill-writer" --skip-final-snapshot > /dev/null
	aws rds wait db-instance-deleted --db-instance-identifier "$cluster-$drill-writer"
	aws rds delete-db-cluster --db-cluster-identifier "$cluster-$drill" --skip-final-snapshot > /dev/null
	aws dynamodb delete-table --table-name "$table-$drill" > /dev/null
	aws s3 rm "s3://$target" --recursive > /dev/null
	aws s3api list-object-versions --bucket "$target" --output json \
		| jq -r '(.Versions // []) + (.DeleteMarkers // []) | .[] | [.Key, .VersionId] | @tsv' \
		| while IFS=$'\t' read -r key version; do aws s3api delete-object --bucket "$target" --key "$key" --version-id "$version" > /dev/null; done
	aws s3api delete-bucket --bucket "$target"
	echo "Removed the restored resources."
fi
