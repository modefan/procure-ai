<#
.SYNOPSIS
  Builds the container images of procure-ai (plan 07 Task 1): 13 .NET apps, the Angular web app, Postgres, Keycloak and Debezium.
.DESCRIPTION
  Images are named procure-ai-<name>:<Tag> in the local Docker engine:
    access-api approvals-api assistant-api audit-api budgets-api invoicing-api notifications-api purchasing-api requests-api
    suppliers-api gateway simulators demo-data web postgres keycloak debezium
  With -Registry (e.g. ghcr.io/modefan) each image is also tagged <Registry>/procure-ai-<name>:<Tag>; with -Push those are pushed
  (log in to the registry first; this script never handles credentials). With -ImportTo <cluster> the images are loaded into that
  k3d cluster (`k3d image import`). Images are built one after another so the PC stays responsive.
.EXAMPLE
  deploy/scripts/build-images.ps1 -Tag local-test
  deploy/scripts/build-images.ps1 -Tag 1a2b3c4 -Registry ghcr.io/modefan -Push
  deploy/scripts/build-images.ps1 -Tag dev -ImportTo procure
#>
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Registry = '',
    [switch]$Push,
    [string]$ImportTo = '',
    # Only some images, by name without the procure-ai- prefix (e.g. -Only web,keycloak). Default: all.
    [string[]]$Only = @(),
    # Target platform of the .NET images (the container's OS and CPU).
    [string]$RuntimeIdentifier = 'linux-x64'
)
$ErrorActionPreference = 'Stop'
# `pwsh -File ... -Only a,b` passes one string "a,b"; accept both forms.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if ($Push -and -not $Registry) { throw '-Push needs -Registry (e.g. ghcr.io/modefan).' }
if ($ImportTo -and -not (Get-Command k3d -ErrorAction SilentlyContinue)) { throw "-ImportTo $ImportTo needs k3d on the PATH." }

# name -> project (built with the .NET SDK container support)
$dotnet = [ordered]@{
    'access-api'        = 'src/Services/Access/ProcureAi.Access.Api'
    'approvals-api'     = 'src/Services/Approvals/ProcureAi.Approvals.Api'
    'assistant-api'     = 'src/Services/Assistant/ProcureAi.Assistant.Api'
    'audit-api'         = 'src/Services/Audit/ProcureAi.Audit.Api'
    'budgets-api'       = 'src/Services/Budgets/ProcureAi.Budgets.Api'
    'invoicing-api'     = 'src/Services/Invoicing/ProcureAi.Invoicing.Api'
    'notifications-api' = 'src/Services/Notifications/ProcureAi.Notifications.Api'
    'purchasing-api'    = 'src/Services/Purchasing/ProcureAi.Purchasing.Api'
    'requests-api'      = 'src/Services/Requests/ProcureAi.Requests.Api'
    'suppliers-api'     = 'src/Services/Suppliers/ProcureAi.Suppliers.Api'
    'gateway'           = 'src/Gateway/ProcureAi.Gateway'
    'simulators'        = 'src/Simulators/ProcureAi.Simulators'
    'demo-data'         = 'tools/ProcureAi.DemoData'
}
# name -> Dockerfile folder (also the build context)
$dockerfiles = [ordered]@{
    'web'      = 'src/Web/procure-ai-web'
    'postgres' = 'infra/postgres'
    'keycloak' = 'infra/keycloak'
    'debezium' = 'infra/debezium'
}

function Run([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$built = [System.Collections.Generic.List[string]]::new()
function Wanted([string]$name) { $Only.Count -eq 0 -or $Only -contains $name }

foreach ($name in $dotnet.Keys) {
    if (-not (Wanted $name)) { continue }
    $project = Join-Path $root $dotnet[$name]
    Write-Host "== procure-ai-${name}:$Tag (dotnet publish)"
    Run "dotnet publish $name" {
        dotnet publish $project -c Release -m:4 -t:PublishContainer "-p:ImageTag=$Tag" "-p:ContainerRuntimeIdentifier=$RuntimeIdentifier" --nologo -v:minimal
    }
    $built.Add("procure-ai-${name}:$Tag")
}

foreach ($name in $dockerfiles.Keys) {
    if (-not (Wanted $name)) { continue }
    $context = Join-Path $root $dockerfiles[$name]
    Write-Host "== procure-ai-${name}:$Tag (docker build)"
    Run "docker build $name" { docker build --tag "procure-ai-${name}:$Tag" $context }
    $built.Add("procure-ai-${name}:$Tag")
}

if ($Registry) {
    $Registry = $Registry.TrimEnd('/')
    foreach ($image in $built) {
        Run "docker tag $image" { docker tag $image "$Registry/$image" }
        if ($Push) {
            Write-Host "== push $Registry/$image"
            Run "docker push $Registry/$image" { docker push "$Registry/$image" }
        }
    }
}

if ($ImportTo) {
    Write-Host "== k3d image import into cluster '$ImportTo'"
    $names = if ($Registry) { $built | ForEach-Object { "$Registry/$_" } } else { $built }
    Run 'k3d image import' { k3d image import @names --cluster $ImportTo }
}

Write-Host "`nBuilt $($built.Count) image(s):"
$built | ForEach-Object { Write-Host "  $_" }
