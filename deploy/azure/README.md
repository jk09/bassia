# Bassia on Azure

The reference deployment of Bassia in Azure (feature record `bassia-681e8902`): **bassia-server**, **Bassia Studio**
(the web client, signed in with Microsoft Entra ID) and **agentic runs** as Azure Container Apps job executions, from
versioned Terraform in one command.

```
                       Microsoft Entra ID (sign-in, app roles Reader / Contributor / Admin)
                                   |
  browser ── HTTPS ──> Bassia Studio (Container App, 1 replica) ── start/stop executions ──┐
                           |   ^ live output (per-run token)                                v
  bassia CLI ── HTTPS ──> bassia-server (Container App, 1-3 replicas) <── clone, land ── run job (Container Apps job,
                           |                 |                                           one execution per run:
                Blob Storage (objects)   PostgreSQL (heads, path index, leases)         bassia agent job + Claude Code)
                                   Key Vault: server token, PostgreSQL connection, agent API key, Studio's client secret
```

| Resource | Purpose |
|---|---|
| Container Apps environment + Log Analytics | Hosts the apps and the job; logs |
| `bassia-server` container app | The monorepo's API, landing service and dashboard; Blob Storage and PostgreSQL behind it |
| `bassia-studio` container app | The web client; starts runs as job executions with its managed identity |
| `bassia-runs` container app job | One execution per agentic run (image `bassia-worker`: bassia + git + Claude Code) |
| Storage account (ZRS, Entra ID only) | Objects: chunks, trees, commits, operations, views |
| Azure Database for PostgreSQL flexible server | Heads, the path index and leases |
| Key Vault (RBAC) | `server-token`, `postgres-connection`, `anthropic-api-key`, `studio-entra-client-secret` |
| Container Registry | `bassia-server`, `bassia-studio`, `bassia-worker` images |
| Managed identities (server, studio, worker) | Least privilege: blobs for the server, the job for Studio, secrets and image pulls for each |
| Entra ID app registration | Studio's sign-in and its app roles |

## Deploy

Prerequisites: an Azure subscription where you may create resources and role assignments, permission to create an
Entra ID app registration, the Azure CLI (`az login`) and Terraform 1.6 or later.

```sh
cp deploy/azure/terraform/terraform.tfvars.example deploy/azure/terraform/terraform.tfvars   # fill it in
deploy/azure/deploy.sh                 # or deploy/azure/deploy.ps1 on Windows
```

The script applies the foundation, builds the three images in the registry with ACR Tasks (no local Docker), then
applies the apps and the run job, and prints the outputs: `studio_url`, `server_url` and the command that prints the
server token. Re-run it to deploy a new version (images are tagged with the commit).

After the first deployment:

1. Set the agent's key, unless you passed `anthropic_api_key`:
   `az keyvault secret set --vault-name <key_vault> --name anthropic-api-key --value <key>`.
2. Give people Studio roles: `studio_admins` / `studio_contributors` in `terraform.tfvars`, or in the Entra admin
   center under Enterprise applications > Bassia Studio > Users and groups. Signed-in people without a role are readers.
3. Clone the server from a laptop: `bassia clone -from <server_url> -token "$(<server_token_command>)"`.

## Choices and limits of the reference deployment

- Studio runs one replica: live run output and its sign-in keys are held in memory. Several replicas need a shared
  Data Protection key ring and sticky sessions (Blazor Server circuits).
- PostgreSQL authenticates with a password from Key Vault and accepts connections from Azure services; for production,
  put the environment and PostgreSQL in a virtual network with private endpoints (and Blob Storage and Key Vault too).
- A run's container gets 2 vCPU and 4 GiB (`run_cpu`, `run_memory`) for up to two hours (`run_timeout_seconds`).
- The server token is shared by every client of the server; people are told apart in Studio (Entra ID), and runs are
  recorded with the Studio user who started them.

## Check without Azure

```sh
cd deploy/azure/terraform && terraform fmt -check && terraform init -backend=false && terraform validate
for image in server studio worker; do docker build -f deploy/docker/$image.Dockerfile -t bassia-$image:test .; done
TAG=test deploy/docker/smoke.sh        # server, Studio and a run in the worker image, on this machine
```

CI runs the same checks (job "Azure deployment").
