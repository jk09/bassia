#!/usr/bin/env bash
# Deploys Bassia to Azure in one command: bassia-server, Bassia Studio and the agentic run job (README.md).
#   deploy/azure/deploy.sh [terraform apply options, e.g. -auto-approve]
# Needs: az (signed in: az login), terraform, and deploy/azure/terraform/terraform.tfvars. Images are built in the
# registry by ACR Tasks, so no local Docker is needed.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/deploy/azure/terraform"
tag="${IMAGE_TAG:-$(git -C "$root" rev-parse --short HEAD)}"

terraform init -input=false
# 1. Everything but the apps: their images are not in the registry yet.
terraform apply -input=false -var "image_tag=$tag" -var deploy_apps=false "$@"
registry="$(terraform output -raw registry)"

# 2. The images, built from this checkout in the registry.
for image in server studio worker; do
  az acr build --registry "$registry" --image "bassia-$image:$tag" --file "$root/deploy/docker/$image.Dockerfile" "$root"
done

# 3. The apps and the run job on those images.
terraform apply -input=false -var "image_tag=$tag" "$@"
terraform output
