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
    # Tag of the app images (and of the platform images unless -ContentTagPlatform). Not needed with -List.
    [string]$Tag = '',
    [string]$Registry = '',
    [switch]$Push,
    [string]$ImportTo = '',
    # Only some images, by name without the procure-ai- prefix (e.g. -Only web,keycloak). Default: all.
    [string[]]$Only = @(),
    # Target platform of the .NET images (the container's OS and CPU).
    [string]$RuntimeIdentifier = 'linux-x64',
    # Label org.opencontainers.image.source of every image. GitHub Container Registry links a package to the repository this names, which
    # lets the repository's workflows (GITHUB_TOKEN) push and delete its versions. Without it the web image would carry its nginx base
    # image's label (github.com/nginx/docker-nginx-unprivileged).
    [string]$SourceUrl = 'https://github.com/modefan/procure-ai',
    # The platform images (postgres, keycloak, debezium: built from infra/<name>) get the git tree hash of their build context as tag
    # (`git rev-parse HEAD:infra/<name>`) instead of -Tag, so they change only when their files change and an app-only deploy does not
    # restart Postgres, Keycloak or Debezium (plan 07 Task 6). The folder must have no uncommitted changes.
    [switch]$ContentTagPlatform,
    # With -Registry and -ContentTagPlatform: a platform image whose tag is already in the registry is neither built nor pushed.
    [switch]$SkipExisting,
    # Writes the platform images' tags as JSON ({"postgres": "<tag>", ...}) to this file (the publish workflow reads it).
    [string]$TagsFile = '',
    # Prints the image names (without procure-ai-) as a JSON array and exits; the one list other tools read (e.g. retention.yml).
    [switch]$List
)
$ErrorActionPreference = 'Stop'
# `pwsh -File ... -Only a,b` passes one string "a,b"; accept both forms.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
# Label org.opencontainers.image.revision: the commit the images are built from.
$revision = (& git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $revision) { $revision = 'unknown' }

$platformImages = @('postgres', 'keycloak', 'debezium')
if (-not $List -and -not $Tag) { throw '-Tag is required (the commit SHA, or local-<sha7>).' }
if ($SkipExisting -and -not ($Registry -and $ContentTagPlatform)) { throw '-SkipExisting needs -Registry and -ContentTagPlatform.' }

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

if ($List) {
    ConvertTo-Json -Compress -InputObject @(@($dotnet.Keys) + @($dockerfiles.Keys))
    return
}

function Get-ImageTag([string]$name) {
    if (-not ($ContentTagPlatform -and $platformImages -contains $name)) { return $Tag }
    $folder = $dockerfiles[$name]
    $dirty = & git -C $root status --porcelain -- $folder
    if ($LASTEXITCODE -ne 0) { throw "git status of $folder failed." }
    if ($dirty) { throw "$folder has uncommitted changes; commit them first (the platform image tag is the folder's committed tree hash)." }
    $hash = & git -C $root rev-parse "HEAD:$folder"
    if ($LASTEXITCODE -ne 0 -or $hash -notmatch '^[0-9a-f]{40}$') { throw "git rev-parse HEAD:$folder failed." }
    $hash
}

function Test-InRegistry([string]$image) {
    & docker manifest inspect "$($Registry.TrimEnd('/'))/$image" *> $null
    $LASTEXITCODE -eq 0
}

$platformTags = [ordered]@{}
$skipped = [System.Collections.Generic.List[string]]::new()
$built = [System.Collections.Generic.List[string]]::new()
function Wanted([string]$name) { $Only.Count -eq 0 -or $Only -contains $name }

foreach ($name in $dotnet.Keys) {
    if (-not (Wanted $name)) { continue }
    $project = Join-Path $root $dotnet[$name]
    Write-Host "== procure-ai-${name}:$Tag (dotnet publish)"
    Run "dotnet publish $name" {
        # PublishRepositoryUrl: the SDK labels the image with the repository (source) and commit (revision) from git.
        dotnet publish $project -c Release -m:4 -t:PublishContainer "-p:ImageTag=$Tag" "-p:ContainerRuntimeIdentifier=$RuntimeIdentifier" `
            -p:PublishRepositoryUrl=true "-p:RepositoryUrl=$SourceUrl" --nologo -v:minimal
    }
    $built.Add("procure-ai-${name}:$Tag")
}

foreach ($name in $dockerfiles.Keys) {
    if (-not (Wanted $name)) { continue }
    $context = Join-Path $root $dockerfiles[$name]
    $imageTag = Get-ImageTag $name
    if ($platformImages -contains $name) { $platformTags[$name] = $imageTag }
    if ($SkipExisting -and $platformImages -contains $name -and (Test-InRegistry "procure-ai-${name}:$imageTag")) {
        Write-Host "== procure-ai-${name}:$imageTag is already in ${Registry}: not built, not pushed"
        $skipped.Add("procure-ai-${name}:$imageTag")
        continue
    }
    Write-Host "== procure-ai-${name}:$imageTag (docker build)"
    Run "docker build $name" {
        docker build --tag "procure-ai-${name}:$imageTag" --label "org.opencontainers.image.source=$SourceUrl" `
            --label "org.opencontainers.image.revision=$revision" $context
    }
    $built.Add("procure-ai-${name}:$imageTag")
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
    # @(...): with one image (-Only) the pipeline yields a plain string, which splats to nothing ("no valid images specified").
    $names = @(if ($Registry) { $built | ForEach-Object { "$Registry/$_" } } else { $built })
    Run 'k3d image import' { k3d image import @names --cluster $ImportTo }
}

if ($TagsFile) {
    ConvertTo-Json -InputObject $platformTags | Set-Content -Path $TagsFile -Encoding utf8NoBOM
    Write-Host "Platform image tags written to $TagsFile"
}

Write-Host "`nBuilt $($built.Count) image(s):"
$built | ForEach-Object { Write-Host "  $_" }
if ($skipped.Count) { Write-Host "Already in the registry ($($skipped.Count)):"; $skipped | ForEach-Object { Write-Host "  $_" } }
