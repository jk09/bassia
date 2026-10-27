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

The server (`bassia-f245cd5f`) is stateless; its state is a PostgreSQL database (registry, users, permissions,
records, locks, jobs, audit), S3 (component packs, task cache, logs) and DynamoDB (refs, object index and commit graph
of `bassia-ce722c6c`). Today CI (`.github/workflows/ci.yml`) builds
packages per platform but nothing builds a server image or deploys anything. AWS CodeCommit is closed to new
customers, so it is not an option for histories.

## Acceptance criteria

- [ ] Infrastructure as code (AWS CDK in TypeScript) in `deploy/aws` creates: a VPC across 3 availability zones
      (private subnets for compute and data, gateway endpoints for S3 and DynamoDB), the server as an ECS Fargate
      service behind an ALB with a TLS certificate (ACM) and WAF, Aurora PostgreSQL (multi-AZ, encrypted, automated
      backups, point-in-time restore), an S3 bucket (versioning, KMS encryption, lifecycle to cheaper tiers for old
      packs), and the DynamoDB tables of `bassia-ce722c6c` (on-demand capacity, point-in-time recovery, encryption).
- [ ] Server tasks get ephemeral storage sized for the scratch git area of `bassia-ce722c6c` (Fargate allows up to
      200 GiB per task); the compute-heavy operations (merges, splits, verification checkouts) run as separate Fargate
      tasks when a single request would hold a server task too long.
- [ ] The server image is built and published (ECR) by CI from a version tag; a deployment updates the service with a
      rolling update and no downtime, running database migrations first.
- [ ] Autoscaling of the server on CPU and request rate, within configured bounds; the database and histories sized
      for the scale targets, with the limits documented.
- [ ] Single sign-on through the chosen identity provider; secrets in Secrets Manager; least-privilege IAM roles per
      component of the system; no public database or bucket.
- [ ] Observability: logs, metrics and traces (CloudWatch, or OpenTelemetry to the operator's backend) with a
      dashboard and alarms for errors, latency, queue depth, database load and storage growth.
- [ ] Backups and recovery: database point-in-time restore and history backups are tested by a documented restore
      drill; the recovery point and time objectives are stated.
- [ ] Agent workers: a CDK construct and a script that run agents as ECS Fargate tasks in the same region, each
      cloning from the server with a run-scoped credential (`bassia clone -lazy`, a view or `agent start -spec`), with
      ephemeral storage for its working copies and hydrated content.
- [ ] Cost: a cost estimate for the target scale and per additional 1,000 users / 100 concurrent agents, and budgets
      with alerts.
- [ ] `docs/deploy-aws.md` covers prerequisites, deploy, upgrade, scale, restore and tear-down; a staging deployment
      is created and destroyed by CI on demand to prove the code works.

## Approach

Write the infrastructure as CDK stacks: network, data (Aurora, S3, DynamoDB, KMS), compute (server service, compute
tasks), edge (ALB, WAF, ACM, DNS), observability and workers, each deployable on its own. Build a
container image of `bassia serve` in CI. Start with one region; keep region-specific values in configuration so a
second region is a new stack, not a fork. Load-test a staging deployment at the target scale (concurrent runs holding
locks, clones, views loading files, integrations landing) before calling it production-ready.

## Decisions

- One region first; multi-region (data residency, latency) is a later record.
- No CodeCommit (closed to new customers).
- AWS CDK (TypeScript) and ECS Fargate for the server, compute tasks and agent workers. (User decision.) MicroVM
  sandboxes with more isolation (EKS with Kata or Firecracker) can follow if agents need them.
- One organization per deployment, ~1,000 users and ~500 concurrent agents. (User decision.)
- Histories in S3 and DynamoDB (`bassia-ce722c6c`), no git repositories on EBS. (User decision.)

## Open questions

- Which AWS region first, and any data-residency constraints?
- Monthly budget for the first deployment.

## Progress

- [ ] Network, data and edge modules
- [ ] Server image and service with rolling deploys
- [ ] Identity, secrets, IAM
- [ ] Observability and alarms
- [ ] Backups and restore drill
- [ ] Workers
- [ ] Load test at target scale; docs; staging in CI

## Validation

- `cdk deploy` of a staging environment from a clean account, then the two-client scenario of `bassia-f245cd5f`
  against it.
- A load test at the target scale with the results recorded here.
- A restore drill into a fresh environment with the measured recovery time recorded here.
