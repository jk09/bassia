# Deploying the Bassia server on AWS

The Bassia server (`bassia-server`, see "Bassia server" in [reference.md](reference.md)) on AWS: an ECS Fargate
service across three availability zones behind a load balancer with TLS and a web application firewall, Aurora
PostgreSQL for its records, S3 and DynamoDB for the component histories, CloudWatch for logs, metrics and alarms, AWS
Backup, and agent workers as Fargate tasks in the same region. The infrastructure is a CDK app (TypeScript) in
[`deploy/aws`](../deploy/aws); the images are built from [`deploy/docker`](../deploy/docker). The first region is
eu-central-1 (Frankfurt); another region is another configuration file.

## What it creates

| Stack | What |
| --- | --- |
| `<env>-network` | A VPC across three zones: public subnets (load balancer, NAT gateways), private compute subnets (the server, its tasks, workers), isolated data subnets (the database); gateway endpoints for S3 and DynamoDB, so history traffic never crosses a NAT gateway; flow logs of rejected traffic. |
| `<env>-data` | One KMS key (rotated); the histories' bucket (versioned, KMS, private, TLS only, Intelligent-Tiering after 30 days, old versions expired after 35 days); the DynamoDB table (on demand, point-in-time recovery, KMS); Aurora PostgreSQL Serverless v2 (writer and reader in two zones, encrypted, TLS only, automated backups, point-in-time restore); the server's signing key and the database credentials in Secrets Manager; the image repositories (ECR, scanned, tags immutable in production); in production, a daily AWS Backup plan over the database, the table and the bucket (35 days). |
| `<env>-server` | The ECS cluster; the server service (1 vCPU, 4 GiB, 100 GiB ephemeral storage for the git cache per task by default), rolling deployments that never drop below the desired count and roll back when new tasks do not get healthy; autoscaling on CPU (60%) and requests per task; the load balancer (HTTPS with an ACM certificate validated in Route 53, HTTP redirected; 15 minute idle timeout for long clones) with access logs; the WAF (a rate limit per address, AWS's IP reputation, known bad inputs and common rule sets, the body rules only counted since pushes carry binary packs); the DNS record; the garbage collection task on a schedule; an OpenTelemetry collector (ADOT) beside the server in each task, writing the server's metrics to CloudWatch (namespace `Bassia`, as embedded metrics in `/<env>/metrics`). |
| `<env>-workers` | The agent worker task (the `bassia` CLI, git and an agent; Fargate Spot by default) with ephemeral storage for working copies, and what `scripts/run-agent.sh` needs to start one. |
| `<env>-observability` | A dashboard; alarms to an SNS topic with an email subscription; the monthly AWS Budget with alerts at 80% (actual) and 100% (forecast). |

Every task of the server's service runs the database migrations in a container of its own first (`bassia-server
migrate`) and the server only once it succeeded; migrations wait for each other on a PostgreSQL advisory lock, so the
tasks of a deployment that start together migrate once. A migration must keep the previous release working (add
columns and tables; remove them a release later), since old and new tasks overlap during a rolling deployment.

What the server does itself is light: requests, git upload-pack and receive-pack on its cache, publishing to S3 and
DynamoDB. The heavy work - merges, splits, verification checkouts - runs where runs and integrations run: on people's
machines and agent workers. The garbage collection of the histories is a scheduled task of the server image
(`bassia-server gc`).

## Prerequisites

- An AWS account, and the AWS CLI, Node.js 22, jq and Docker on the machine (or CI) that deploys.
- The account bootstrapped for the CDK in the region: `npx cdk bootstrap aws://<account>/eu-central-1`.
- A DNS name for the server in a Route 53 hosted zone of the account (or an ACM certificate for it made elsewhere).
- An identity provider (any OpenID Connect provider - Okta, Entra ID, Cognito, IAM Identity Center - and/or a GitHub
  OAuth app with the device flow enabled). For OIDC, register a public client for the command line with the loopback
  redirect `http://127.0.0.1/callback/` (any port) and PKCE, and note the issuer, the client id and the audience of its
  access tokens; add the `groups` claim to use the provider's groups in grants.
- For the workflows, an IAM role GitHub Actions may assume through OIDC (trust `token.actions.githubusercontent.com`
  for this repository) with the permissions to deploy CDK stacks (the CDK bootstrap roles), push to ECR, run ECS tasks
  and read CloudWatch logs; its ARN in the repository variable `AWS_DEPLOY_ROLE_ARN`, and a GitHub environment
  `production` whose reviewers approve deployments.

## Configure

`deploy/aws/config/<environment>.json` describes an environment; `production.json` and `staging.json` are examples.
Set at least the DNS name and hosted zone (`domainName`, `hostedZoneName`, `hostedZoneId`, or `certificateArn`), the
admins (`admins`: user ids that hold every permission, even before they are registered), the identity providers
(`oidc`, `githubClientId`), where alarms go (`alarmEmail`) and the budget (`monthlyBudgetUsd`).

The dashboard's sign-in in a browser (`https://<domainName>/login`) works with an OIDC provider's public client
(PKCE) as it is. A confidential client, and GitHub's web flow, need a client secret. Keep the secrets in a Secrets
Manager secret made outside the stacks, a JSON object with `github` for the GitHub OAuth app's secret and the name of
an OIDC provider for that provider's client secret. Then name it and its keys in `webSignIn`:

```sh
aws secretsmanager create-secret --name bassia/web-sign-in --secret-string '{"github": "...", "sso": "..."}'
```

```json
"webSignIn": { "secretName": "bassia/web-sign-in", "github": true, "oidc": ["sso"] }
```

Register `https://<domainName>/login/<name>/callback` (`github` for GitHub) as each client's redirect URL. The alarms
on the server's own metrics take their thresholds from `metricAlarms` (`lockConflictsPer5Minutes`, default 50;
`mergeQueueDepth`, default 20). Sizes are in `server`,
`database` and `workers`. `retainData: true` (production) keeps the data, the image repositories and the logs when a
stack is deleted, and turns on deletion protection; it also requires a domain, since production is served over HTTPS.
`npm test` in `deploy/aws` checks the synthesized templates; `npx cdk synth -c env=<environment>` shows them.

## Deploy

```sh
cd deploy/aws && npm ci
npx cdk deploy bassia-network bassia-data -c env=production        # the image repositories exist from here on
# build and push deploy/docker/server.Dockerfile and worker.Dockerfile with the tag of config.server.imageTag
npx cdk deploy --all -c env=production
```

The Deploy workflow (`.github/workflows/deploy.yml`) does exactly this for a pushed `v<semver>` tag, with
`-c imageTag=<tag>`, after the `production` environment's reviewers approve. Then make the first admin a user:

```sh
deploy/aws/scripts/server-task.sh -env bassia token -user admin -minutes 30    # a token for a configured admin
bassia login -server https://bassia.example.com -token <token>
bassia clone -from https://bassia.example.com -path monorepo && cd monorepo
bassia user add -id admin -name "The Admin" -email admin@example.com           # the admin, registered
bassia user add -id alice -name Alice -email alice@example.com                 # users log in with SSO from now on
bassia access grant -to group:engineering -permission run
bassia component import -url https://github.com/example/lib.git                # the server imports it
```

`deploy/aws/scripts/server-task.sh -env <environment> check` checks that bucket versioning and point-in-time recovery
are on.

## Upgrade

Push a `v<semver>` tag; the Deploy workflow publishes its images and deploys them. The service rolls over task by task:
each new task migrates (a no-op after the first), passes the load balancer's health check (`/readyz`), and only then
does an old one drain (60 seconds) and stop. A deployment whose tasks do not get healthy is rolled back by the
circuit breaker. Images are immutable in production: a tag is deployed as it was built. A rollback is a deployment of
the previous tag (`workflow_dispatch` with `tag`), which works as long as the migrations in between kept it working.

## Scale

The server scales between `server.minTasks` and `server.maxTasks` on CPU (60%) and requests per task. The database
scales between `database.minAcu` and `database.maxAcu` Aurora capacity units; the table and the bucket have no capacity
to manage. The defaults are for ~1,000 users and ~500 concurrent agents; the scale targets and their measurement are in
`feat/active/bassia-b9581afe/SPEC.md` (or `feat/done/...`) and `feat/done/bassia-ce722c6c/SPEC.md`. What limits a
deployment:

- A task's git cache is bounded by its ephemeral storage (at most 200 GiB): a cold task downloads the packs of the
  components it serves first; more tasks mean more caches, each of the components its requests hit.
- One landing moves at most 100 refs (DynamoDB's transaction limit), and landings on one component wait for each
  other (the merge queue).
- The lock table has one version: concurrent lock decisions retry. Watch the request rate and latency of `/api/v1/locks`
  in the load test.
- The WAF's rate limit counts requests per address; agent workers share the NAT gateways' addresses, so
  `rateLimitPer5Minutes` must cover all of them together.
- The load balancer closes connections idle for 15 minutes.

## Agent workers

```sh
bassia login -server https://bassia.example.com                                # who the run is for (needs 'run')
deploy/aws/scripts/run-agent.sh -env bassia -components app,lib -spec parser.spec -prompt "make the parser handle tabs"
```

The script makes a worker token for the run's components (`bassia token create`: `run` at most, those components
only, 240 minutes by default) and starts an ECS task of the worker image on Fargate Spot: it logs in with the token,
clones the server lazily, maps the spec and runs the agent (`deploy/docker/worker-entrypoint.sh`); the result is pushed
to the server and the run is visible to everyone allowed to read its components (`bassia agent list|show|logs`). The
worker image's agent is chosen at build time (`--build-arg AGENT_PACKAGES=@anthropic-ai/claude-code@<version>`); its
key comes from the secret `workers.agentSecretName` (as `ANTHROPIC_API_KEY`). Workers hold no AWS permissions. The token
travels as a task override, readable by whoever may describe the cluster's tasks, and expires with its lifetime.

## Observe

The dashboard (CloudWatch, named after the environment) shows requests, errors, response time, tasks, CPU and memory,
database capacity and connections, table throttling and the bucket's size. It also shows the server's own metrics
(see "Metrics" in [reference.md](reference.md)): response time, the merge queue (landings waiting and landing, and
their wait), lock decisions (committed, decided again, waits, conflicts), git traffic (bytes pushed and fetched,
fetches and pushes), database and storage latency, and the browsers following live changes.

The server sends those metrics over OTLP every minute to the collector in its task (ADOT,
`ServerStack.collectorImage`). The collector writes them as embedded metrics to `/<env>/metrics`, with the dimensions
the dashboard and the alarms use (`ServerStack.metricDimensions`); the other labels stay in the log, for Logs
Insights. The collector is not essential: without it, the server serves on without metrics.

Alarms go to the SNS topic `<env>-alarms`:
- the server: errors, latency (p99 above 10 s), unhealthy tasks, scaling at its maximum, CPU;
- the database: capacity and CPU;
- the table: throttling and errors;
- storage growth;
- lock conflicts (runs keep colliding on the same paths);
- a long merge queue.

Logs are in `/<env>/server` (one JSON line per request; the migrate, gc and otel-collector streams beside them) and
`/<env>/workers`.

## Restore

What is kept: the database's point-in-time restore (`database.backupDays`, to any second, the latest about 5 minutes
ago), the table's point-in-time recovery (35 days, to any second), every version of every object in the bucket for 35
days after it was replaced or deleted, and - in production - daily AWS Backup recovery points of all three (35 days).

| Objective | Value |
| --- | --- |
| Recovery point (data lost), a mistake or corruption in the region | at most 5 minutes |
| Recovery point, the loss of the region | 24 hours with backups copied to another region (a later record; today the backups stay in the region) |
| Recovery time | 2 hours, to be confirmed by the drill below |

To restore, pick one moment T and restore all three to it - refs at T only reach packs that existed at T:

1. Restore the database to T (`restore-db-cluster-to-point-in-time`) as a new cluster, with a writer instance.
2. Restore the table to T (`restore-table-to-point-in-time`) as a new table.
3. Restore the bucket to T: copy each key's version current at T into a new bucket (S3 Batch Operations for a large
   bucket; the drill script copies object by object).
4. Point the environment at the new resources: set them in the configuration's stacks (or rename them into place),
   deploy, and let the tasks start with empty caches.

`deploy/aws/scripts/restore-drill.sh -env <environment> [-at <time>] [-cleanup]` rehearses steps 1-3 beside the live
environment, checks a server task on the restored set (migrations, and a garbage collection that reads every history)
and prints how long it took; the Staging workflow runs it on demand (`restore-drill`). Record the measured time in the
feature record.

## Tear down

`npx cdk destroy --all -c env=<environment>`. A staging environment (`retainData: false`) goes completely. A production
environment keeps its data: the bucket, the table (with deletion protection), a final snapshot of the database, the
key, the image repositories, the backup vault and the logs stay until deleted by hand.

## Staging

The Staging workflow (`.github/workflows/staging.yml`, run on demand) deploys `config/staging.json` from the current
commit - one NAT gateway, one server task, a small database, served over HTTP at the load balancer's name - publishes
the images with the tag `staging`, runs the two-client scenario (`deploy/aws/scripts/staging-check.sh`), and on request
the load test (`scripts/Invoke-CloudLoadTest.ps1`) and the restore drill, writing the results to the run's summary,
then destroys the environment (unless `keep`).

## Cost

Approximate list prices in eu-central-1, on demand, per month; check them with the AWS Pricing Calculator.

| Part (production defaults) | Per month |
| --- | --- |
| Server: 2 Fargate tasks of 1 vCPU and 4 GiB, 100 GiB ephemeral storage each, the daily GC task | ~$115 |
| Load balancer (hours and capacity units) and WAF (web ACL, rules, ~20 million requests) | ~$70 |
| NAT gateways: 3 (hours and ~200 GB processed) | ~$125 |
| Aurora Serverless v2: writer and reader averaging 1 ACU each, 20 GB, I/O | ~$215 |
| DynamoDB on demand (reads, transactional writes), point-in-time recovery | ~$20 |
| S3: 200 GB of packs (1,000 components of ~200 MB), old versions, requests | ~$10 |
| AWS Backup (database, table, bucket; 35 days) | ~$15 |
| CloudWatch (logs ~30 GB, Container Insights, alarms, dashboard, ~70 custom metrics of the server at $0.30 each), KMS, Secrets Manager, ECR, Route 53 | ~$75 |
| Data out to users outside AWS (~300 GB of clones and fetches) | ~$25 |
| **Server side, for ~1,000 users** | **~$670** |

Agent workers are priced by the hour they run: a worker of 2 vCPU and 8 GiB costs ~$0.13 an hour on demand and ~$0.04
on Fargate Spot. The $1,500 budget leaves ~$850 for workers: ~21,000 agent-hours a month on Spot, e.g. 500 concurrent
agents for 2 hours on each of 21 working days. 500 agents around the clock on Spot would cost ~$14,600 a month.

| Growth | Per month |
| --- | --- |
| Each additional 1,000 users | ~$200-250: one or two server tasks, ~1 more Aurora ACU on average, storage and logs |
| Each additional 100 concurrent agents | ~$4 an hour of agent work on Spot (~$13 on demand), plus ~$50 for a server task |
| The staging environment while it is up | ~$6 a day (one NAT gateway, one task, a minimal database) |

The budget alarm (`monthlyBudgetUsd`) covers the whole account; give workers an account of their own, or a cost
allocation tag filter, to budget them separately.
