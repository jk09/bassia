# Studio's sign-in: an Entra ID app registration whose app roles are Studio's roles.

resource "random_uuid" "role" {
  for_each = toset(["Reader", "Contributor", "Admin"])
}

locals {
  studio_roles = {
    Reader      = "Browse versions, workspaces and runs."
    Contributor = "Create and change own workspaces and leases, land, and start agentic runs."
    Admin       = "Act on anyone's workspaces, leases and runs."
  }
}

resource "azuread_application" "studio" {
  display_name     = "Bassia Studio (${var.name})"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azurerm_client_config.current.object_id]

  dynamic "app_role" {
    for_each = local.studio_roles
    content {
      allowed_member_types = ["User"]
      description          = app_role.value
      display_name         = app_role.key
      enabled              = true
      id                   = random_uuid.role[app_role.key].result
      value                = app_role.key
    }
  }

  web {
    redirect_uris = ["${local.studio_url}/signin-oidc"]
    logout_url    = "${local.studio_url}/signout-callback-oidc"

    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  optional_claims {
    id_token {
      name = "email"
    }
  }
}

resource "azuread_service_principal" "studio" {
  client_id                    = azuread_application.studio.client_id
  app_role_assignment_required = false
  owners                       = [data.azurerm_client_config.current.object_id]
}

resource "azuread_application_password" "studio" {
  application_id = azuread_application.studio.id
  display_name   = "bassia-studio"
  end_date       = timeadd(timestamp(), "8760h")

  lifecycle {
    ignore_changes = [end_date]
  }
}

resource "azuread_app_role_assignment" "admins" {
  for_each            = toset(var.studio_admins)
  app_role_id         = random_uuid.role["Admin"].result
  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.studio.object_id
}

resource "azuread_app_role_assignment" "contributors" {
  for_each            = toset(var.studio_contributors)
  app_role_id         = random_uuid.role["Contributor"].result
  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.studio.object_id
}
