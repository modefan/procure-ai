<#
.SYNOPSIS
  Checks the container images built by build-images.ps1 (plan 07 Task 1). Exit code 1 on any failure.
.DESCRIPTION
  Everything it starts is named procure-ai-imgtest-* and removed again at the end (also on failure); every container has a memory
  limit. Throwaway passwords are generated per run and never written anywhere. Run: deploy/scripts/test-images.ps1 -Tag local-test
#>
param(
    [Parameter(Mandatory)][string]$Tag
)
$ErrorActionPreference = 'Continue'

$dotnetImages = 'access-api', 'approvals-api', 'assistant-api', 'audit-api', 'budgets-api', 'invoicing-api', 'notifications-api',
    'purchasing-api', 'requests-api', 'suppliers-api', 'gateway', 'simulators', 'demo-data'
$otherImages = 'web', 'postgres', 'keycloak', 'debezium'
$allImages = $dotnetImages + $otherImages

$failures = [System.Collections.Generic.List[string]]::new()
function Pass([string]$what) { Write-Host "  ok    $what" }
function Fail([string]$what, [string]$detail = '') {
    Write-Host "  FAIL  $what $detail" -ForegroundColor Red
    $failures.Add($what)
}
function Check([bool]$ok, [string]$what, [string]$detail = '') { if ($ok) { Pass $what } else { Fail $what $detail } }
function Image([string]$name) { "procure-ai-${name}:$Tag" }
function RandomSecret { -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ }) }
function Head([string]$text, [int]$length = 300) { if ($text.Length -gt $length) { $text.Substring(0, $length) } else { $text } }

$prefix = 'procure-ai-imgtest'
$network = "$prefix-net"
function Cleanup {
    $ids = docker ps -aq --filter "name=^$prefix" 2>$null
    if ($ids) { docker rm -f $ids *> $null }
    docker network rm $network *> $null
}

try {
    Cleanup

    Write-Host "Images exist (tag $Tag)"
    foreach ($n in $allImages) {
        docker image inspect (Image $n) *> $null
        Check ($LASTEXITCODE -eq 0) "image $(Image $n) exists"
    }

    Write-Host 'Images run as a non-root user'
    foreach ($n in $allImages) {
        # The Postgres image starts as root only to drop to the postgres user in the official entrypoint; checked at runtime below.
        if ($n -eq 'postgres') { continue }
        $user = (docker image inspect (Image $n) --format '{{.Config.User}}' 2>$null)
        Check ($LASTEXITCODE -eq 0 -and $user -and $user -notin 'root', '0', '0:0') "$n runs as '$user'"
    }

    Write-Host '.NET images: the entrypoint runs the app, and invalid configuration fails at startup'
    foreach ($n in $dotnetImages) {
        $appArgs = @()
        if ($n -eq 'demo-data') { $appArgs = @('seed') } elseif ($n -like '*-api') { $appArgs = @('--migrate') }
        $out = (docker run --rm --name "$prefix-$n" --memory 512m --cpus 1 (Image $n) @appArgs 2>&1 | Out-String)
        $code = $LASTEXITCODE
        Check ($code -ne 0 -and $out -match '(?i)(validation|failed|required|must be|missing|not configured|Authority|ConnectionStrings)') `
            "$n exits non-zero with a configuration message" "(exit $code) $(Head $out)"
    }

    Write-Host 'web: SPA fallback, cache headers, mounted config.json'
    $cfgDir = Join-Path ([IO.Path]::GetTempPath()) "$prefix-web-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $cfgDir | Out-Null
    $marker = "mounted-$([guid]::NewGuid().ToString('N'))"
    Set-Content -Path (Join-Path $cfgDir 'config.json') -Value "{ `"apiBaseUrl`": `"$marker`" }" -Encoding utf8
    $cfgMount = (Resolve-Path (Join-Path $cfgDir 'config.json')).Path
    docker run -d --name "$prefix-web" --memory 128m --cpus 1 -p 127.0.0.1::8080 `
        -v "${cfgMount}:/usr/share/nginx/html/config.json:ro" (Image 'web') *> $null
    $webPort = ((docker port "$prefix-web" 8080/tcp | Select-Object -First 1) -split ':')[-1].Trim()
    $base = "http://127.0.0.1:$webPort"
    $ready = $false
    for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
        try { Invoke-WebRequest "$base/" -UseBasicParsing -TimeoutSec 2 *> $null; $ready = $true } catch { Start-Sleep -Seconds 1 }
    }
    Check $ready 'web answers on 8080'
    if ($ready) {
        $index = Invoke-WebRequest "$base/" -UseBasicParsing
        $deep = Invoke-WebRequest "$base/requests/123" -UseBasicParsing
        Check ($index.Content -match '<app-root') '/ serves index.html'
        Check ($deep.StatusCode -eq 200 -and $deep.Content -eq $index.Content) '/requests/123 falls back to index.html'
        Check (($index.Headers['Cache-Control'] -join ',') -match 'no-cache') 'index.html is no-cache'
        Check (($deep.Headers['Cache-Control'] -join ',') -match 'no-cache') 'the SPA fallback is no-cache'
        $cfg = Invoke-WebRequest "$base/config.json" -UseBasicParsing
        Check ($cfg.Content -match [regex]::Escape($marker)) 'the mounted config.json wins'
        Check (($cfg.Headers['Cache-Control'] -join ',') -match 'no-cache') 'config.json is no-cache'
        $asset = [regex]::Match($index.Content, '(?:src|href)="/?([^"]+-[A-Z0-9]{8}\.(?:js|css))"').Groups[1].Value
        Check ($asset -ne '') 'index.html references a hashed asset' (Head $index.Content)
        if ($asset) {
            $a = Invoke-WebRequest "$base/$asset" -UseBasicParsing
            $cc = $a.Headers['Cache-Control'] -join ','
            Check ($cc -match 'max-age=31536000' -and $cc -match 'immutable' -and $cc -match 'public') "hashed asset $asset is immutable" $cc
        }
    }
    Remove-Item -Recurse -Force $cfgDir

    Write-Host 'postgres + keycloak: optimized start against a throwaway Postgres, realm discovery document'
    docker network create $network *> $null
    $pgPassword = RandomSecret
    $kcAdminPassword = RandomSecret
    docker run -d --name "$prefix-pg" --network $network --network-alias pg --memory 512m --cpus 1 `
        -e "POSTGRES_PASSWORD=$pgPassword" -e POSTGRES_DB=keycloak -e "DEBEZIUM_PASSWORD=$(RandomSecret)" (Image 'postgres') `
        -c wal_level=logical -c shared_preload_libraries=pgaudit -c shared_buffers=64MB *> $null
    $pgReady = $false
    for ($i = 0; $i -lt 60 -and -not $pgReady; $i++) {
        docker exec "$prefix-pg" psql -U postgres -d keycloak -tAc 'select 1' *> $null
        if ($LASTEXITCODE -eq 0) {
            # The init scripts run on a temporary server first; ready twice in a row (2 s apart) means the real one is up.
            Start-Sleep -Seconds 2
            docker exec "$prefix-pg" psql -U postgres -d keycloak -tAc 'select 1' *> $null
            $pgReady = ($LASTEXITCODE -eq 0)
        }
        if (-not $pgReady) { Start-Sleep -Seconds 1 }
    }
    Check $pgReady 'postgres image starts'
    if ($pgReady) {
        $uid = (docker exec "$prefix-pg" sh -c "awk '/^Uid:/ {print `$2}' /proc/1/status").Trim()
        Check ($uid -and $uid -ne '0') "postgres server runs as uid $uid (not root)"
        $wal = (docker exec "$prefix-pg" psql -U postgres -tAc 'show wal_level').Trim()
        Check ($wal -eq 'logical') "postgres wal_level is $wal"
        $ext = (docker exec "$prefix-pg" psql -U postgres -tAc "select count(*) from pg_available_extensions where name in ('vector','pgaudit')").Trim()
        Check ($ext -eq '2') 'pgvector and pgaudit are available'
    }

    $kcArgs = @(
        'run', '-d', '--name', "$prefix-kc", '--network', $network, '--memory', '1g', '--cpus', '2', '-p', '127.0.0.1::8080',
        '-e', 'KC_DB_URL=jdbc:postgresql://pg:5432/keycloak', '-e', 'KC_DB_USERNAME=postgres', '-e', "KC_DB_PASSWORD=$pgPassword",
        '-e', 'KC_HTTP_ENABLED=true', '-e', 'KC_HOSTNAME_STRICT=false', '-e', 'JAVA_OPTS_KC_HEAP=-Xms256m -Xmx640m',
        '-e', 'KC_BOOTSTRAP_ADMIN_USERNAME=admin', '-e', "KC_BOOTSTRAP_ADMIN_PASSWORD=$kcAdminPassword",
        '-e', "KC_DEMO_PASSWORD=$(RandomSecret)", '-e', "KC_GATEWAY_SECRET=$(RandomSecret)", '-e', "KC_INTERNAL_SECRET=$(RandomSecret)",
        '-e', "KC_ACCESS_ADMIN_SECRET=$(RandomSecret)", '-e', "KC_DEMO_LOGIN_SECRET=$(RandomSecret)",
        (Image 'keycloak'), 'start', '--optimized', '--import-realm')
    docker @kcArgs *> $null
    $kcPort = ((docker port "$prefix-kc" 8080/tcp | Select-Object -First 1) -split ':')[-1].Trim()
    $discovered = $false
    for ($i = 0; $i -lt 120 -and -not $discovered; $i++) {
        try {
            $d = Invoke-RestMethod "http://127.0.0.1:$kcPort/realms/procure/.well-known/openid-configuration" -TimeoutSec 3
            $discovered = ($d.issuer -match '/realms/procure$')
        } catch { Start-Sleep -Seconds 2 }
    }
    Check $discovered 'keycloak (start --optimized) serves the procure realm discovery document'
    if (-not $discovered) { docker logs --tail 30 "$prefix-kc" 2>&1 | Out-Host }

    Write-Host 'debezium: both properties files'
    foreach ($f in 'application.properties', 'application-mongo.properties') {
        $out = (docker run --rm --entrypoint cat --memory 64m (Image 'debezium') "/debezium/config/$f" 2>&1 | Out-String)
        Check ($LASTEXITCODE -eq 0 -and $out -match 'debezium\.sink\.type=rabbitmq') "debezium image holds /debezium/config/$f"
    }
    $mongo = (docker run --rm --entrypoint cat --memory 64m (Image 'debezium') '/debezium/config/application-mongo.properties' 2>&1 | Out-String)
    Check ($mongo -match 'MongoDbConnector') 'application-mongo.properties is the MongoDB one'
}
catch {
    Fail 'the test script itself failed' "$_"
}
finally {
    Cleanup
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nAll image checks passed." -ForegroundColor Green
exit 0
