# Bassia server on AWS

Phase 1, part 4 of 4 of hosting Bassia as a scalable service. Deploys `bassia-f245cd5f` (Bassia server) with the S3
and DynamoDB storage of `bassia-ce722c6c`, both building on `bassia-e13e1548` (shared-state contracts).

## Outcome

An operator deploys a production Bassia server to an AWS account with one command from versioned infrastructure code:
highly available across availability zones, autoscaled, backed up, observable, and secured. Users reach it at an HTTPS
endpoint with single sign-on; agent workers run in the same region and download what they load at network speed.
The deployment is sized for one organization of ~1,000 users and ~500 concurrent agents, and grows without
re-architecture.

## Context

The server (`bassia-server`, `bassia-f245cd5f`) is stateless; its state is a PostgreSQL database (records, locks, jobs,
task cache index, identities, grants, audit), and the cloud storage of `bassia-ce722c6c`: an S3 bucket (component
packs, the server's blobs) and one DynamoDB table (refs, pack lists, the meta-repo history). Each server task keeps a
disposable git cache of the components it serves. CI (`.github/workflows/ci.yml`) builds packages per platform, but
nothing builds a server image or deploys anything. AWS CodeCommit is closed to new customers, so it is not an option
for histories.

## Acceptance criteria

- [x] Infrastructure as code (AWS CDK in TypeScript) in `deploy/aws` creates, in eu-central-1 by default and with every
      region-specific value in configuration: a VPC across 3 availability zones (private subnets for compute, isolated
      subnets for data, gateway endpoints for S3 and DynamoDB), the server as an ECS Fargate service behind an ALB with a
      TLS certificate (ACM) and WAF, Aurora PostgreSQL (Serverless v2, writer and reader in different zones, encrypted,
      automated backups, point-in-time restore), an S3 bucket (versioning, KMS encryption, lifecycle to cheaper tiers
      for old objects, expiry of old versions), and the DynamoDB table of `bassia-ce722c6c` (on-demand capacity,
      point-in-time recovery, KMS encryption). Each concern is its own stack.
- [x] Server tasks get ephemeral storage sized for their git cache (Fargate allows up to 200 GiB per task). The
      compute-heavy work of the server runs as separate Fargate tasks: garbage collection of the histories
      (`bassia-server gc`) on a schedule, database migrations before each deployment. Merges, splits and verification
      checkouts run where the runs and integrations run - on clients and agent workers - not in the server.
- [x] The server image is built and published (ECR) by CI from a version tag; a deployment runs the database migrations
      as a one-off task first, then updates the service with a rolling update (circuit breaker, no capacity below the
      desired count) and no downtime. Migrations are safe to run from several tasks at once.
- [x] Autoscaling of the server on CPU and request rate, within configured bounds; the database and histories sized
      for the scale targets, with the limits documented.
- [x] Single sign-on through any OIDC provider and GitHub (`bassia-f245cd5f`), configured per environment; secrets
      (signing key, database credentials, provider settings that are secret) in Secrets Manager; least-privilege IAM
      roles per part of the system (server, migration and GC tasks, workers); no public database or bucket.
- [x] Observability: logs (CloudWatch, JSON request lines), metrics of the load balancer, service, database, table
      and bucket on a dashboard, and alarms (to an SNS topic, email) for errors, latency, unhealthy tasks, scaling at
      its maximum, database load, table throttling and storage growth. Traces and the server's own metrics (queue
      depth) come with `bassia-d3c6cfe4`.
- [x] Backups and recovery: Aurora point-in-time restore, DynamoDB point-in-time recovery, S3 versioning, and an AWS
      Backup plan over all three; a documented restore drill (and a script) into a fresh environment; the recovery
      point and time objectives are stated.
- [x] Agent workers: a CDK construct and a script that run agents as ECS Fargate tasks in the same region (Fargate Spot
      by default), each logging in with a worker token scoped to its components (`bassia token create`), cloning the
      server (`bassia clone -lazy`) and running `agent start -spec`, with ephemeral storage for its working copies and
      hydrated content.
- [x] Cost: an estimate for the target scale and per additional 1,000 users / 100 concurrent agents, and an AWS Budget
      of $1,500 a month with alerts. (User decision.)
- [x] `docs/deploy-aws.md` covers prerequisites, deploy, upgrade, scale, restore and tear-down. CI synthesizes and
      tests the CDK app on every push; a staging deployment is created, exercised and destroyed by a workflow on
      demand, which runs once the repository has an AWS role to assume.

## Approach

CDK stacks: network, data (Aurora, S3, DynamoDB, KMS, secrets, backups), server (ECR image, ECS service, ALB, WAF,
ACM, DNS, migration and GC tasks), workers, observability (dashboard, alarms, budget), each deployable on its own and
wired by references. A container image of `bassia-server` (and one of the `bassia` CLI for workers) built in CI. One
region first; region-specific values in a configuration file per environment, so a second region is a new
configuration, not a fork. Assertion tests check the synthesized templates for the properties above.

## Decisions

- One region first; multi-region (data residency, latency) is a later record.
- No CodeCommit (closed to new customers).
- AWS CDK (TypeScript) and ECS Fargate for the server, its tasks and agent workers. (User decision.) MicroVM
  sandboxes with more isolation (EKS with Kata or Firecracker) can follow if agents need them.
- One organization per deployment, ~1,000 users and ~500 concurrent agents. (User decision.)
- Histories in S3 and DynamoDB (`bassia-ce722c6c`), no git repositories on EBS. (User decision.)
- Region eu-central-1 (Frankfurt) first, for EU data residency. (User decision.)
- Monthly budget $1,500 for the first deployment: the budget alarm's amount and the default sizing (Aurora Serverless
  v2 from 0.5 ACU, two server tasks, one NAT gateway in staging). Agent workers on Fargate Spot. (User decision.)
- No AWS account or Docker in the build environment: the CDK app is synthesized and tested in CI now; the staging
  deployment, the two-client scenario, the load test and the restore drill run from the on-demand workflow once an
  AWS role is configured, and this record stays active until their results are recorded. (User decision.)

Made during implementation:

- Migrations run in every server task, in a container the server waits for (`dependsOn` SUCCESS), not as a separate
  task before the service update: no orchestration outside CloudFormation, and an advisory lock makes concurrent
  migrations safe. A migration must keep the previous release working while old and new tasks overlap.
- The server is configured by environment variables in containers (`ServerConfig.FromEnvironment`), the secrets
  injected by ECS from Secrets Manager; the secrets use Secrets Manager's own key, since one of the data key would
  write the server's roles into its key policy and make the data stack depend on the server stack.
- The AWS Backup plan is production's only (`retainData`): a vault holding recovery points cannot be deleted, which
  would keep staging from being destroyed; staging relies on point-in-time recovery.
- Staging without a domain is served over HTTP at the load balancer's name; a configuration that retains data must
  have a domain and is served over HTTPS only.
- The WAF counts (does not block) the common rule set's body rules: pushes carry binary packs of any size. Its rate
  limit is per address, and agent workers share the NAT gateways' addresses.
- Hosted zones are named by id in the configuration (no lookups at synthesis), so the app synthesizes without AWS
  credentials - in CI and in tests.
- Operator jobs are one-off tasks of the server image (`deploy/aws/scripts/server-task.sh`: `token`, `check`, `gc`,
  `migrate`), which the staging check and the restore drill use too.

## Cost estimate

Approximate eu-central-1 list prices; the details are in `docs/deploy-aws.md` ("Cost").

| | Per month |
| --- | --- |
| Server side at the target scale (2 server tasks, load balancer and WAF, 3 NAT gateways, Aurora Serverless v2 at ~2 ACU, DynamoDB, 200 GB in S3, backups, CloudWatch, data out) | ~$650 |
| Agent workers (2 vCPU, 8 GiB) | ~$0.04 per agent-hour on Fargate Spot (~$0.13 on demand): the rest of the $1,500 budget is ~21,000 agent-hours |
| Each additional 1,000 users | ~$200-250 |
| Each additional 100 concurrent agents | ~$4 per hour of agent work on Spot, plus ~$50 for a server task |

## Progress

- [x] Network, data and edge stacks
- [x] Server image and service with rolling deploys, migrations first
- [x] Identity, secrets, IAM
- [x] Observability, alarms, budget
- [x] Backups and restore drill (script and procedure)
- [x] Workers
- [x] Docs (`docs/deploy-aws.md`); synth and tests in CI; Deploy and Staging workflows
- [ ] Staging deployed, two-client scenario, load test, restore drill - results recorded (needs `AWS_DEPLOY_ROLE_ARN`)

## Validation

- `npm test` and `npx cdk synth` in `deploy/aws` (CI, every push).
- `cdk deploy` of a staging environment from a clean account (the `staging` workflow), then the two-client scenario of
  `bassia-f245cd5f` against it.
- A load test at the target scale (`scripts/Invoke-CloudLoadTest.ps1`) with the results recorded here.
- A restore drill into a fresh environment with the measured recovery time recorded here.
