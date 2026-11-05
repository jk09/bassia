# The monorepo's objects (Blob Storage) and its heads, path index and leases (PostgreSQL).

resource "azurerm_storage_account" "objects" {
  name                            = "st${local.prefix}"
  location                        = azurerm_resource_group.main.location
  resource_group_name             = azurerm_resource_group.main.name
  account_tier                    = "Standard"
  account_replication_type        = "ZRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false # Entra ID only: the server signs in with its managed identity.
  tags                            = var.tags

  blob_properties {
    delete_retention_policy {
      days = 14
    }
    container_delete_retention_policy {
      days = 14
    }
  }
}

resource "azurerm_storage_container" "objects" {
  name                  = "bassia"
  storage_account_id    = azurerm_storage_account.objects.id
  container_access_type = "private"
}

# Read and write objects, and sign user delegation SAS URLs so clients move large chunks directly.
resource "azurerm_role_assignment" "server_blobs" {
  scope                = azurerm_storage_account.objects.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.server.principal_id
}

resource "azurerm_role_assignment" "server_delegator" {
  scope                = azurerm_storage_account.objects.id
  role_definition_name = "Storage Blob Delegator"
  principal_id         = azurerm_user_assigned_identity.server.principal_id
}

resource "random_password" "postgres" {
  length  = 32
  special = false
}

resource "azurerm_postgresql_flexible_server" "state" {
  name                          = "psql-${local.prefix}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  version                       = "16"
  sku_name                      = var.postgres_sku
  storage_mb                    = 32768
  backup_retention_days         = 14
  administrator_login           = "bassia"
  administrator_password        = random_password.postgres.result
  public_network_access_enabled = true
  zone                          = "1"
  tags                          = var.tags
}

resource "azurerm_postgresql_flexible_server_database" "bassia" {
  name      = "bassia"
  server_id = azurerm_postgresql_flexible_server.state.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# Reachable from Azure services (the Container Apps environment) only; see README for private networking.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = "azure-services"
  server_id        = azurerm_postgresql_flexible_server.state.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}
