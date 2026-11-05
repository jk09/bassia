variable "subscription_id" {
  description = "The Azure subscription to deploy into."
  type        = string
}

variable "name" {
  description = "A short prefix for every resource (lowercase letters and digits, 3-12 characters)."
  type        = string
  default     = "bassia"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name))
    error_message = "name is 3-12 lowercase letters and digits, starting with a letter."
  }
}

variable "location" {
  description = "The Azure region."
  type        = string
  default     = "westeurope"
}

variable "image_tag" {
  description = "The tag of the bassia-server, bassia-studio and bassia-worker images in the registry (deploy.sh builds them)."
  type        = string
  default     = "latest"
}

variable "deploy_apps" {
  description = "Whether to create the container apps and the run job (false for the first pass, before the images exist)."
  type        = bool
  default     = true
}

variable "anthropic_api_key" {
  description = "The coding agent's API key (Claude Code in the worker image), kept in Key Vault. Leave empty to set it later in Key Vault."
  type        = string
  default     = ""
  sensitive   = true
}

variable "studio_admins" {
  description = "Object ids of Entra ID users or groups given Studio's Admin role."
  type        = list(string)
  default     = []
}

variable "studio_contributors" {
  description = "Object ids of Entra ID users or groups given Studio's Contributor role (everyone else signed in is a Reader)."
  type        = list(string)
  default     = []
}

variable "postgres_sku" {
  description = "The PostgreSQL flexible server SKU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "run_cpu" {
  description = "vCPUs of one agentic run's container."
  type        = number
  default     = 2
}

variable "run_memory" {
  description = "Memory of one agentic run's container (Container Apps pairs it with the CPU, e.g. 2 vCPU with 4Gi)."
  type        = string
  default     = "4Gi"
}

variable "run_timeout_seconds" {
  description = "The longest an agentic run may take."
  type        = number
  default     = 7200
}

variable "tags" {
  description = "Tags on every resource."
  type        = map(string)
  default     = { app = "bassia" }
}
