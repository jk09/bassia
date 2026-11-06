# bassia-server, Bassia Studio and the job that runs agentic runs (one execution per run, started by Studio).

resource "azurerm_container_app" "server" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = local.server_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.server.id]
  }

  registry {
    server   = local.registry
    identity = azurerm_user_assigned_identity.server.id
  }

  secret {
    name                = "server-token"
    key_vault_secret_id = azurerm_key_vault_secret.server_token.versionless_id
    identity            = azurerm_user_assigned_identity.server.id
  }

  secret {
    name                = "postgres-connection"
    key_vault_secret_id = azurerm_key_vault_secret.postgres.versionless_id
    identity            = azurerm_user_assigned_identity.server.id
  }

  # Clients (the CLI on laptops, Studio, run jobs) reach the server over HTTPS with the token.
  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "server"
      image  = "${local.registry}/bassia-server:${var.image_tag}"
      cpu    = 1
      memory = "2Gi"

      env {
        name  = "BASSIA_SERVER_URL"
        value = "http://0.0.0.0:8080"
      }
      env {
        name  = "BASSIA_SERVER_PUBLIC_URL"
        value = local.server_url
      }
      env {
        name  = "BASSIA_SERVER_DATA"
        value = "/tmp/bassia"
      }
      env {
        name        = "BASSIA_SERVER_TOKEN"
        secret_name = "server-token"
      }
      env {
        name  = "BASSIA_SERVER_STORAGE"
        value = "azure"
      }
      env {
        name  = "BASSIA_SERVER_AZURE_URL"
        value = "${azurerm_storage_account.objects.primary_blob_endpoint}${azurerm_storage_container.objects.name}"
      }
      env {
        name  = "BASSIA_SERVER_STATE"
        value = "postgres"
      }
      env {
        name        = "BASSIA_SERVER_POSTGRES"
        secret_name = "postgres-connection"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.server.client_id
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/healthz"
      }
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull, azurerm_role_assignment.read_secrets, azurerm_role_assignment.server_blobs]
}

resource "azurerm_container_app_job" "runs" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = local.job_name
  location                     = azurerm_resource_group.main.location
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  replica_timeout_in_seconds   = var.run_timeout_seconds
  replica_retry_limit          = 0
  tags                         = var.tags

  # Studio starts one execution per run, with the run's environment.
  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.worker.id]
  }

  registry {
    server   = local.registry
    identity = azurerm_user_assigned_identity.worker.id
  }

  secret {
    name                = "server-token"
    key_vault_secret_id = azurerm_key_vault_secret.server_token.versionless_id
    identity            = azurerm_user_assigned_identity.worker.id
  }

  secret {
    name                = "anthropic-api-key"
    key_vault_secret_id = azurerm_key_vault_secret.anthropic.versionless_id
    identity            = azurerm_user_assigned_identity.worker.id
  }

  template {
    container {
      name    = "agent"
      image   = "${local.registry}/bassia-worker:${var.image_tag}"
      cpu     = var.run_cpu
      memory  = var.run_memory
      command = ["bassia"]
      args    = ["agent", "job"]

      env {
        name  = "BASSIA_JOB_SERVER"
        value = local.server_url
      }
      env {
        name        = "BASSIA_JOB_TOKEN"
        secret_name = "server-token"
      }
      env {
        name        = "ANTHROPIC_API_KEY"
        secret_name = "anthropic-api-key"
      }
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull, azurerm_role_assignment.read_secrets]
}

# Studio starts and stops the job's executions with its managed identity.
resource "azurerm_role_assignment" "studio_runs" {
  count                = var.deploy_apps ? 1 : 0
  scope                = azurerm_container_app_job.runs[0].id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.studio.principal_id
}

resource "azurerm_container_app" "studio" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = local.studio_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.studio.id]
  }

  registry {
    server   = local.registry
    identity = azurerm_user_assigned_identity.studio.id
  }

  secret {
    name                = "server-token"
    key_vault_secret_id = azurerm_key_vault_secret.server_token.versionless_id
    identity            = azurerm_user_assigned_identity.studio.id
  }

  secret {
    name                = "entra-client-secret"
    key_vault_secret_id = azurerm_key_vault_secret.studio_client_secret.versionless_id
    identity            = azurerm_user_assigned_identity.studio.id
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # Blazor Server keeps a circuit per browser tab on one replica.
    # (One replica below: live run output and sign-in keys are held in memory.)
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "studio"
      image  = "${local.registry}/bassia-studio:${var.image_tag}"
      cpu    = 1
      memory = "2Gi"

      env {
        name  = "BASSIA_STUDIO_SERVER"
        value = local.server_url
      }
      env {
        name        = "BASSIA_STUDIO_TOKEN"
        secret_name = "server-token"
      }
      env {
        name  = "BASSIA_STUDIO_LABEL"
        value = "${var.name} · Azure"
      }
      env {
        name  = "BASSIA_STUDIO_PUBLIC_URL"
        value = local.studio_url
      }
      env {
        name  = "BASSIA_STUDIO_AUTH"
        value = "entra"
      }
      env {
        name  = "BASSIA_STUDIO_ENTRA_TENANT"
        value = data.azurerm_client_config.current.tenant_id
      }
      env {
        name  = "BASSIA_STUDIO_ENTRA_CLIENT_ID"
        value = azuread_application.studio.client_id
      }
      env {
        name        = "BASSIA_STUDIO_ENTRA_CLIENT_SECRET"
        secret_name = "entra-client-secret"
      }
      env {
        name  = "BASSIA_STUDIO_RUN_HOST"
        value = "aca"
      }
      env {
        name  = "BASSIA_STUDIO_ACA_JOB"
        value = azurerm_container_app_job.runs[0].id
      }
      env {
        name  = "BASSIA_STUDIO_ACA_IMAGE"
        value = "${local.registry}/bassia-worker:${var.image_tag}"
      }
      env {
        name  = "BASSIA_STUDIO_ACA_CPU"
        value = tostring(var.run_cpu)
      }
      env {
        name  = "BASSIA_STUDIO_ACA_MEMORY"
        value = var.run_memory
      }
      env {
        name  = "BASSIA_STUDIO_ACA_SECRETS"
        value = "BASSIA_JOB_TOKEN=server-token,ANTHROPIC_API_KEY=anthropic-api-key"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.studio.client_id
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/healthz"
      }
    }
  }

  depends_on = [azurerm_role_assignment.acr_pull, azurerm_role_assignment.read_secrets]
}
