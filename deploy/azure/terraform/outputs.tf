output "studio_url" {
  description = "Bassia Studio (sign in with Entra ID)."
  value       = local.studio_url
}

output "server_url" {
  description = "bassia-server: clone it with 'bassia clone -from <url> -token <token>'."
  value       = local.server_url
}

output "registry" {
  description = "The container registry the images are built into."
  value       = azurerm_container_registry.main.name
}

output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "key_vault" {
  description = "Holds server-token (for CLI clients), anthropic-api-key (the agent's key) and the other secrets."
  value       = azurerm_key_vault.main.name
}

output "server_token_command" {
  description = "Prints the server token for 'bassia clone'."
  value       = "az keyvault secret show --vault-name ${azurerm_key_vault.main.name} --name server-token --query value -o tsv"
}

output "studio_app_id" {
  description = "Studio's Entra ID app registration: assign people its Reader, Contributor and Admin roles."
  value       = azuread_application.studio.client_id
}
