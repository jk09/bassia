# Deploys Bassia to Azure in one command (Windows): see deploy.sh and README.md.
#   ./deploy/azure/deploy.ps1 [terraform apply options, e.g. -auto-approve]
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location (Join-Path $root 'deploy/azure/terraform')
try {
	$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { (git -C $root rev-parse --short HEAD).Trim() }
	terraform init -input=false
	if ($LASTEXITCODE) { throw 'terraform init failed' }
	terraform apply -input=false -var "image_tag=$tag" -var deploy_apps=false @args
	if ($LASTEXITCODE) { throw 'terraform apply (foundation) failed' }
	$registry = terraform output -raw registry
	foreach ($image in 'server', 'studio', 'worker') {
		az acr build --registry $registry --image "bassia-${image}:$tag" --file (Join-Path $root "deploy/docker/$image.Dockerfile") $root
		if ($LASTEXITCODE) { throw "az acr build of bassia-$image failed" }
	}
	terraform apply -input=false -var "image_tag=$tag" @args
	if ($LASTEXITCODE) { throw 'terraform apply (apps) failed' }
	terraform output
}
finally {
	Pop-Location
}
