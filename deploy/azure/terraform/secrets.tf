# Secrets live in Key Vault; the apps and the run job reference them through their managed identities.

resource "azurerm_key_vault" "main" {
  name                       = "kv-${local.prefix}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
  tags                       = var.tags
}

# Whoever deploys writes the secrets.
resource "azurerm_role_assignment" "deployer_secrets" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "read_secrets" {
  for_each = {
    server = azurerm_user_assigned_identity.server.principal_id
    studio = azurerm_user_assigned_identity.studio.principal_id
    worker = azurerm_user_assigned_identity.worker.principal_id
  }
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = each.value
}

resource "random_password" "server_token" {
  length  = 48
  special = false
}

resource "azurerm_key_vault_secret" "server_token" {
  name         = "server-token"
  value        = random_password.server_token.result
  key_vault_id = azurerm_key_vault.main.id
  depends_on   = [azurerm_role_assignment.deployer_secrets]
}

resource "azurerm_key_vault_secret" "postgres" {
  name         = "postgres-connection"
  value        = "Host=${azurerm_postgresql_flexible_server.state.fqdn};Database=${azurerm_postgresql_flexible_server_database.bassia.name};Username=${azurerm_postgresql_flexible_server.state.administrator_login};Password=${random_password.postgres.result};SSL Mode=Require"
  key_vault_id = azurerm_key_vault.main.id
  depends_on   = [azurerm_role_assignment.deployer_secrets]
}

resource "azurerm_key_vault_secret" "anthropic" {
  name         = "anthropic-api-key"
  value        = var.anthropic_api_key == "" ? "set-me" : var.anthropic_api_key
  key_vault_id = azurerm_key_vault.main.id
  depends_on   = [azurerm_role_assignment.deployer_secrets]

  lifecycle {
    # Set or rotated in Key Vault directly once deployed.
    ignore_changes = [value]
  }
}

resource "azurerm_key_vault_secret" "studio_client_secret" {
  name         = "studio-entra-client-secret"
  value        = azuread_application_password.studio.value
  key_vault_id = azurerm_key_vault.main.id
  depends_on   = [azurerm_role_assignment.deployer_secrets]
}
