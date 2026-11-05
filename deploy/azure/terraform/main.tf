# Bassia on Azure: bassia-server (objects in Blob Storage, state in Azure Database for PostgreSQL), Bassia Studio
# (sign-in with Microsoft Entra ID) and agentic runs as Azure Container Apps job executions - all in one Container Apps
# environment, with secrets in Key Vault and images in Azure Container Registry. See README.md.

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  prefix      = "${var.name}${random_string.suffix.result}"
  server_name = "${var.name}-server"
  studio_name = "${var.name}-studio"
  job_name    = "${var.name}-runs"
  # A container app's address is <app name>.<the environment's default domain>.
  server_url = "https://${local.server_name}.${azurerm_container_app_environment.main.default_domain}"
  studio_url = "https://${local.studio_name}.${azurerm_container_app_environment.main.default_domain}"
  registry   = azurerm_container_registry.main.login_server
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${var.name}"
  location = var.location
  tags     = var.tags
}

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${var.name}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  tags                       = var.tags
}

resource "azurerm_container_registry" "main" {
  name                = "acr${local.prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "Basic"
  admin_enabled       = false
  tags                = var.tags
}

# One identity per component: what it may read and do is granted to it alone.
resource "azurerm_user_assigned_identity" "server" {
  name                = "id-${var.name}-server"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags
}

resource "azurerm_user_assigned_identity" "studio" {
  name                = "id-${var.name}-studio"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags
}

resource "azurerm_user_assigned_identity" "worker" {
  name                = "id-${var.name}-worker"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags
}

resource "azurerm_role_assignment" "acr_pull" {
  for_each = {
    server = azurerm_user_assigned_identity.server.principal_id
    studio = azurerm_user_assigned_identity.studio.principal_id
    worker = azurerm_user_assigned_identity.worker.principal_id
  }
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = each.value
}
