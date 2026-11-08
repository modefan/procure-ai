#Requires -Version 7.4
<#
.SYNOPSIS
    Swagger UI works from the browser with the admin API key, for every service (plan 08b Task 2).
.DESCRIPTION
    Does what Swagger UI does after Dale clicks Authorize and "Try it out", with curl:
    1. /swagger/index.html answers 200 and its config (/swagger/index.js) lists the definitions (10 expected).
    2. Every definition's document answers 200, names exactly one server, and that server is the https address the browser used
       (the gateway URL). An http server here is what broke "Try it out" on 2026-11-08: the https page's calls were blocked as
       mixed content.
    3. Every document offers the security schemes ApiKey (X-Api-Key), TenantId (X-Tenant-Id) and OAuth2.
    4. One GET per service, sent to <the document's server> + path with the admin key, and with X-Tenant-Id only where the operation's
       own security asks for TenantId (as Swagger UI sends it), answers 200.
    5. A tenant endpoint called with the key but without X-Tenant-Id answers 400 (the TenantId requirement is real).
    6. The demo switch: GET /api/v1/system/availability with the key answers 200 with `enabled`. With -PutSwitchSameState also the
       admin-key-only PUT, sending the state it just read (the switch "sets the state": the same body changes nothing). Never on by
       default, so a run never touches the live demo's switch unless asked.

    -Environment apphost: the gateway of a running Aspire AppHost (-GatewayUrl https://localhost:<port>, from the dashboard), key from the
    user env var DEMO_ADMIN_API_KEY. local: the k3d cluster at https://procure.localtest.me (TLS against the local CA), the same key.
    hosted: https://procure.daleabels.com, key from DEMO_ADMIN_API_KEY_HOSTED. curl, not .NET (ESET on Dale's PC intercepts TLS from
    pwsh). The key reaches curl in a header file only the current user can read, never on a command line; nothing secret is printed.
    Exit code 1 on any failure.
.EXAMPLE
    ./tests/Test-Swagger.ps1 -Environment hosted
.EXAMPLE
    ./tests/Test-Swagger.ps1 -Environment apphost -GatewayUrl https://localhost:17711 -PutSwitchSameState
#>
[CmdletBinding()]
param(
    [ValidateSet('apphost', 'local', 'hosted')]
    [string] $Environment = 'hosted',
    [string] $GatewayUrl,
    [string] $Tenant = 'acme',
    [int] $ExpectedDefinitions = 10,
    [switch] $PutSwitchSameState
)

$ErrorActionPreference = 'Stop'
if (-not $GatewayUrl) {
    $GatewayUrl = switch ($Environment) {
        'hosted' { 'https://procure.daleabels.com' }
        'local' { 'https://procure.localtest.me' }
        default { throw '-Environment apphost needs -GatewayUrl (the gateway''s https URL from the Aspire dashboard).' }
    }
}
$GatewayUrl = $GatewayUrl.TrimEnd('/')
$keyVar = if ($Environment -eq 'hosted') { 'DEMO_ADMIN_API_KEY_HOSTED' } else { 'DEMO_ADMIN_API_KEY' }
$key = [Environment]::GetEnvironmentVariable($keyVar, 'Process')
if (-not $key) { $key = [Environment]::GetEnvironmentVariable($keyVar, 'User') }
if (-not $key) { throw "The user env var $keyVar is not set." }

# One GET per service (no path parameters, nothing required): the definition name -> path.
$probes = [ordered]@{
    requests      = '/api/v1/requests'
    access        = '/api/v1/access/cost-centers'
    budgets       = '/api/v1/budgets'
    approvals     = '/api/v1/approvals/inbox'
    audit         = '/api/v1/audit/activity'
    suppliers     = '/api/v1/suppliers'
    purchasing    = '/api/v1/purchasing/orders'
    invoicing     = '/api/v1/invoicing/invoices'
    notifications = '/api/v1/notifications/system-status'
    assistant     = '/api/v1/assistant/info'
}
$switchPath = '/api/v1/system/availability'

$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string] $message) { $failures.Add($message); Write-Host "FAIL: $message" -ForegroundColor Red }
function Pass([string] $message) { Write-Host "ok:   $message" -ForegroundColor Green }

# Git for Windows' curl, else Windows' curl.exe. Both use Schannel: Windows' trusted roots (Let's Encrypt; the ASP.NET Core dev
# certificate once trusted), --cacert for the local cluster's CA, --ssl-no-revoke because neither local certificate has a CRL.
$curl = @('C:\Program Files\Git\mingw64\bin\curl.exe', "$env:SystemRoot\System32\curl.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $curl) { throw 'curl not found.' }
$tlsArgs = @('--ssl-no-revoke')
if ($Environment -eq 'local') {
    $caFile = Join-Path $env:USERPROFILE '.procure-ai\local-ca\ca.crt'
    if (-not (Test-Path $caFile)) { throw "Local CA $caFile not found (procure-ai-gitops bootstrap/new-local-ca.ps1)." }
    $tlsArgs += @('--cacert', $caFile)
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('procure-swagger-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
# Only the current user may read the folder that holds the key's header file.
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -Path $work -AclObject $acl
$keyHeaders = Join-Path $work 'key.txt'
Set-Content -Path $keyHeaders -Value "X-Api-Key: $key" -NoNewline
$key = $null

function Invoke-Curl {
    # Returns @{ Status; Body }. Extra curl arguments after the URL.
    param([Parameter(Mandatory)] [string] $Url, [string[]] $Extra = @())
    $bodyFile = Join-Path $work 'body.txt'
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
    $status = & $curl -sS --max-time 30 @tlsArgs -o $bodyFile -w '%{http_code}' @Extra $Url 2>&1
    $code = if ("$status" -match '(\d{3})$') { [int] $Matches[1] } else { 0 }
    $body = if (Test-Path $bodyFile) { Get-Content -Raw $bodyFile } else { '' }
    if ($code -eq 0) { $body = "$status" }
    [pscustomobject]@{ Status = $code; Body = $body }
}

function Get-Json([string] $text) { try { $text | ConvertFrom-Json -AsHashtable -Depth 100 } catch { $null } }

try {
    Write-Host "Swagger UI at $GatewayUrl/swagger ($Environment)"

    # 1. The page and its definitions, as the browser loads them.
    $page = Invoke-Curl "$GatewayUrl/swagger/index.html"
    if ($page.Status -eq 200) { Pass '/swagger/index.html answers 200' } else { Fail "/swagger/index.html answered $($page.Status)" }
    $config = Invoke-Curl "$GatewayUrl/swagger/index.js"
    $definitions = @()
    if ($config.Body -match '"urls":(\[[^\]]*\])') { $definitions = @($Matches[1] | ConvertFrom-Json) }
    if ($definitions.Count -eq $ExpectedDefinitions) { Pass "Swagger UI lists $($definitions.Count) definitions" }
    else { Fail "Swagger UI lists $($definitions.Count) definitions, expected $ExpectedDefinitions" }

    $documents = @{}
    foreach ($definition in $definitions) {
        $name = $definition.name
        # 2. The document and its server.
        $response = Invoke-Curl ($GatewayUrl + $definition.url)
        $document = Get-Json $response.Body
        if ($response.Status -ne 200 -or -not $document) { Fail "$name document answered $($response.Status)"; continue }
        $documents[$name] = $document
        $servers = @($document.servers | ForEach-Object { $_.url })
        if ($servers.Count -eq 1 -and $servers[0].TrimEnd('/') -eq $GatewayUrl) { Pass "$name server is $($servers[0])" }
        else { Fail "$name servers are '$($servers -join ', ')', expected $GatewayUrl/ (an http server is blocked as mixed content)" }

        # 3. The Authorize dialog's schemes.
        $schemes = $document.components.securitySchemes
        $apiKey = $schemes['ApiKey']; $tenantScheme = $schemes['TenantId']
        if ($apiKey.type -eq 'apiKey' -and $apiKey.in -eq 'header' -and $apiKey.name -eq 'X-Api-Key' -and
            $tenantScheme.type -eq 'apiKey' -and $tenantScheme.in -eq 'header' -and $tenantScheme.name -eq 'X-Tenant-Id' -and
            $schemes['OAuth2'].type -eq 'oauth2') { Pass "$name offers ApiKey, TenantId and OAuth2" }
        else { Fail "$name security schemes are '$(@($schemes.Keys) -join ', ')', expected ApiKey (X-Api-Key), TenantId (X-Tenant-Id), OAuth2" }
    }

    # 4. One key-authorized GET per service, the way Swagger UI sends it.
    foreach ($name in $probes.Keys) {
        $document = $documents[$name]
        if (-not $document) { Fail "$name has no document to call from"; continue }
        $path = $probes[$name]
        $operation = $document.paths[$path]?['get']
        if (-not $operation) { Fail "$name document has no GET $path"; continue }
        $server = @($document.servers)[0].url.TrimEnd('/')
        $needsTenant = @($operation.security | Where-Object { $_.Keys -contains 'TenantId' }).Count -gt 0
        $headers = @('-H', "@$keyHeaders")
        if ($needsTenant) { $headers += @('-H', "X-Tenant-Id: $Tenant") }
        $call = Invoke-Curl "$server$path" $headers
        $what = if ($needsTenant) { "key + X-Tenant-Id $Tenant" } else { 'key' }
        if ($call.Status -eq 200) { Pass "$name GET $path with the $what answers 200" }
        else { Fail "$name GET $path with the $what answered $($call.Status)" }
    }

    # 5. The TenantId requirement is real.
    $noTenant = Invoke-Curl "$GatewayUrl/api/v1/requests" @('-H', "@$keyHeaders")
    if ($noTenant.Status -eq 400) { Pass 'a tenant endpoint with the key but no X-Tenant-Id answers 400' }
    else { Fail "a tenant endpoint with the key but no X-Tenant-Id answered $($noTenant.Status), expected 400" }

    # 6. The demo switch.
    $switch = Invoke-Curl "$GatewayUrl$switchPath" @('-H', "@$keyHeaders")
    $state = Get-Json $switch.Body
    if ($switch.Status -eq 200 -and $null -ne $state -and $state.ContainsKey('enabled')) { Pass "GET $switchPath answers 200 (enabled: $($state.enabled))" }
    else { Fail "GET $switchPath answered $($switch.Status)" }
    if ($PutSwitchSameState -and $state) {
        $bodyFile = Join-Path $work 'switch.json'
        @{ enabled = [bool] $state.enabled; message = $state.message } | ConvertTo-Json -Compress | Set-Content -Path $bodyFile -NoNewline
        $put = Invoke-Curl "$GatewayUrl$switchPath" @('-X', 'PUT', '-H', "@$keyHeaders", '-H', 'Content-Type: application/json', '--data-binary', "@$bodyFile")
        $after = Get-Json $put.Body
        if ($put.Status -eq 200 -and $after -and [bool] $after.enabled -eq [bool] $state.enabled) { Pass "PUT $switchPath (same state) with the key answers 200" }
        else { Fail "PUT $switchPath (same state) answered $($put.Status)" }
        $anonymous = Invoke-Curl "$GatewayUrl$switchPath" @('-X', 'PUT', '-H', 'Content-Type: application/json', '--data-binary', "@$bodyFile")
        if ($anonymous.Status -in 401, 403) { Pass "PUT $switchPath without the key is refused ($($anonymous.Status))" }
        else { Fail "PUT $switchPath without the key answered $($anonymous.Status)" }
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All Swagger checks passed.' -ForegroundColor Green
