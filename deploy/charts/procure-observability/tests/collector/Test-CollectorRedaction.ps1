# Runs the pinned OpenTelemetry Collector image over sample platform logs and checks what comes out: personal data (usernames, e-mail
# addresses, IPv4 and IPv6 addresses) is masked with ***, what is needed to follow an incident (event type, realm, error code, user
# IDs, timestamps, hex IDs) is kept, and pgaudit lines get severity INFO.
#
# It tests the chart's real configuration: the collector config is rendered with `helm template`, and only three things differ from
# the deployed one (written to an override file): the filelog reads the samples from their start, the k8sattributes processor is left
# out (it needs the Kubernetes API) and a file exporter replaces the dashboard exporter. The receiver operators and the transform
# processors are exactly as rendered. The traces pipeline runs its rendered processors too (without k8sattributes), so every OTTL
# statement of the config is parsed, and the collector's log must hold no deprecation notice (e.g. "paths were modified to include their
# context prefix", which the collector logs for OTTL paths in the legacy form without a context prefix; review 12c M1).
#
# Needs: Docker, helm (with the chart's dependency built: `helm dependency build deploy/charts/procure-observability`).
# Usage: pwsh deploy/charts/procure-observability/tests/collector/Test-CollectorRedaction.ps1
[CmdletBinding()]
param(
    [string] $Image = '',
    [string] $MemoryLimit = '256m'
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$chart = Resolve-Path (Join-Path $here '../..')
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("collector-redaction-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$name = "collector-redaction-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-That([bool] $condition, [string] $message) {
    if (-not $condition) { $failures.Add($message) | Out-Null; Write-Host "FAIL: $message" -ForegroundColor Red }
    else { Write-Host "ok:   $message" -ForegroundColor Green }
}

try {
    New-Item -ItemType Directory -Path $work | Out-Null

    # 1. The chart's rendered collector config.
    $rendered = helm template observability $chart --namespace observability --show-only charts/opentelemetry-collector/templates/configmap-agent.yaml
    if ($LASTEXITCODE -ne 0) { throw 'helm template failed' }
    $lines = $rendered -split "`r?`n"
    $start = [array]::IndexOf($lines, '  relay: |')
    if ($start -lt 0) { throw 'relay not found in the rendered ConfigMap' }
    $relayLines = @()
    foreach ($l in $lines[($start + 1)..($lines.Length - 1)]) {
        if ($l -match '^    ' -or $l -eq '') { $relayLines += ($l -replace '^    ', '') } else { break }
    }
    $relay = $relayLines -join "`n"
    if ($Image -eq '') {
        $tag = (Select-String -Path (Join-Path $chart 'values.yaml') -Pattern '^\s+tag:\s*(\S+)' | Select-Object -First 1).Matches[0].Groups[1].Value
        $Image = "otel/opentelemetry-collector-k8s:$tag"
    }

    # The logs pipeline's processors as rendered, without k8sattributes.
    $m = [regex]::Match($relay, '(?s)\n    logs:\s*\n\s*exporters:.*?\n\s*processors:\s*\n((?:\s*- \S+\n)+)')
    if (-not $m.Success) { throw 'logs pipeline not found in the rendered config' }
    $processors = $m.Groups[1].Value -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' } |
        ForEach-Object { $_ -replace '^- ', '' } | Where-Object { $_ -notmatch '^k8s_?attributes' }
    Assert-That ($processors -contains 'transform/redact') 'the logs pipeline runs transform/redact'
    # The traces pipeline's processors as rendered, without k8sattributes (filter/background-db, transform/client-address).
    $t = [regex]::Match($relay, '(?s)\n    traces:\s*\n\s*exporters:.*?\n\s*processors:\s*\n((?:\s*- \S+\n)+)')
    if (-not $t.Success) { throw 'traces pipeline not found in the rendered config' }
    $traceProcessors = $t.Groups[1].Value -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' } |
        ForEach-Object { $_ -replace '^- ', '' } | Where-Object { $_ -notmatch '^k8s_?attributes' }
    Assert-That ($traceProcessors -contains 'filter/background-db') 'the traces pipeline runs filter/background-db'

    # The receiver's name as the chart renders it (file_log or filelog).
    $receiver = [regex]::Match($relay, '(?m)^  (file_?log):').Groups[1].Value
    if ($receiver -eq '') { throw 'filelog receiver not found in the rendered config' }

    $override = @"
receivers:
  ${receiver}:
    start_at: beginning
exporters:
  file:
    path: /out/logs.jsonl
    flush_interval: 500ms
service:
  extensions: []
  pipelines:
    logs:
      receivers: [$receiver]
      processors: [$($processors -join ', ')]
      exporters: [file]
    # The logs pipeline is under test; traces runs its processors only so that their OTTL is parsed (k8sattributes left out, it needs
    # the Kubernetes API); metrics runs none.
    traces:
      receivers: [otlp]
      processors: [$($traceProcessors -join ', ')]
      exporters: [file]
    metrics:
      receivers: [otlp]
      processors: []
      exporters: [file]
"@
    Set-Content -Path (Join-Path $work 'relay.yaml') -Value $relay -Encoding utf8NoBOM
    Set-Content -Path (Join-Path $work 'override.yaml') -Value $override -Encoding utf8NoBOM

    # 2. The samples in the layout containerd writes: /var/log/pods/<namespace>_<pod>_<uid>/<container>/<restart>.log
    $pods = Join-Path $work 'pods'
    $expected = 0
    foreach ($f in Get-ChildItem (Join-Path $here 'samples') -Filter *.log) {
        $container = $f.BaseName
        $dir = Join-Path $pods "procure_$container-0_00000000-0000-0000-0000-000000000001/$container"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        # LF line ends, as containerd writes them (a Windows checkout may have CRLF).
        [System.IO.File]::WriteAllText((Join-Path $dir '0.log'), ((Get-Content $f.FullName -Raw) -replace "`r`n", "`n"))
        $expected += @(Get-Content $f.FullName | Where-Object { $_.Trim() -ne '' }).Count
    }
    New-Item -ItemType Directory -Path (Join-Path $work 'out') | Out-Null
    # The collector runs as a non-root user; on Linux the output folder must be writable for it.
    if (-not $IsWindows) { chmod -R 777 (Join-Path $work 'out') }

    # 3. Run the collector until every sample line came out (or 60 s).
    docker run -d --name $name --memory $MemoryLimit --cpus 1 -e MY_POD_IP=127.0.0.1 -e K8S_NODE_NAME=test-node `
        -v "$($work)/relay.yaml:/cfg/relay.yaml:ro" -v "$($work)/override.yaml:/cfg/override.yaml:ro" `
        -v "$($pods):/var/log/pods:ro" -v "$($work)/out:/out" `
        $Image --config=/cfg/relay.yaml --config=/cfg/override.yaml | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker run failed' }
    $outFile = Join-Path $work 'out/logs.jsonl'
    $records = @()
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Test-Path $outFile) {
            $records = @()
            try { $content = @(Get-Content $outFile -ErrorAction Stop) } catch { continue }
            foreach ($line in $content) {
                if ($line.Trim() -eq '') { continue }
                # The exporter may be mid-write: a truncated last line is skipped now and read whole on the next poll.
                try { $doc = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
                foreach ($rl in $doc.resourceLogs) { foreach ($sl in $rl.scopeLogs) { foreach ($r in $sl.logRecords) {
                    $records += [pscustomobject]@{ Body = $r.body.stringValue; Severity = $r.severityText }
                } } }
            }
            if ($records.Count -ge $expected) { break }
        }
    }
    if ($records.Count -lt $expected) {
        Write-Host (docker logs $name 2>&1 | Where-Object { $_ -match "Error|error|warn" } | Select-Object -First 15 | Out-String)
    }
    Assert-That ($records.Count -eq $expected) "all $expected sample lines came out (got $($records.Count))"
    $all = ($records | ForEach-Object { $_.Body }) -join "`n"
    Set-Content -Path (Join-Path $work 'out/bodies.txt') -Value $all
    if ($env:REDACTION_TEST_SHOW -eq '1') { Write-Host $all }

    # 4. Personal data is gone.
    foreach ($secret in 'mallory.invented', 'dana.invented', 'admin.invented', 'erin.invented', 'buyer.invented', 'example.com',
                        '203.0.113.', '2001:db8', 'Mary', 'Jane', 'Zed', 'Display', 'Hinted', 'hinted.bare', 'ffff') {
        Assert-That (-not $all.Contains($secret)) "output holds no '$secret'"
    }
    Assert-That ($all -match '(?m)auth_type="code", username="\*\*\*"\s*$') 'a quoted username with a space is masked whole'
    Assert-That ($all -match 'email="\*\*\*", username="\*\*\*"') 'a quoted display-name e-mail and username are masked whole'
    Assert-That ($all -match 'login_hint="\*\*\*"') 'a quoted login_hint is masked whole'
    Assert-That ($all -match '(?m)login_hint="\*\*\*"\s*$') 'a bare login_hint is masked'
    Assert-That ($all -match 'client \*\*\*:44321 rejected for realm procure \(error=forwarded_not_trusted\)') 'an IPv4 with a port inside a Keycloak line is masked, the rest stays'
    Assert-That ($all -match 'email="\*\*\*", username="\*\*\*"\s*$' -or $all -match '(?m)error="user_not_found", email="\*\*\*", username="\*\*\*"\s*$') 'bare email and username are masked'
    Assert-That ($all -match 'username="\*\*\*"') 'username is masked as ***'
    Assert-That ($all -match 'ipAddress="\*\*\*"') 'ipAddress is masked as ***'
    Assert-That ($all -match 'host=\*\*\* port=51234') 'a Postgres host IPv4 is masked'
    Assert-That ($all -match 'accepting AMQP connection \*\*\*:51234 -> \*\*\*:5672') 'RabbitMQ IPv4 endpoints are masked'
    Assert-That ($all -match 'accepting AMQP connection \[\*\*\*\]:51240 -> \[\*\*\*\]:5672') 'RabbitMQ IPv6 endpoints are masked'

    # 5. What helps an incident stays.
    Assert-That ($all.Contains('type="LOGIN_ERROR"')) 'the event type LOGIN_ERROR is kept'
    Assert-That ($all.Contains('realmName="procure"')) 'the realm is kept'
    Assert-That ($all -match 'error="?invalid_user_credentials') 'the error code invalid_user_credentials is kept'
    Assert-That ($all.Contains('userId="3fa85f64-5717-4562-b3fc-2c963f66afa6"')) 'the user UUID is kept'
    Assert-That ($all.Contains('code_id="7b3e1c9a-2d4f-4a6b-9c8d-1e2f3a4b5c6d"')) 'a code UUID is kept'
    Assert-That ($all.Contains('2026-11-05 12:06:41,123 WARN')) 'the Keycloak timestamp is kept'
    Assert-That ($all.Contains('2026-11-05 12:06:41.100 UTC [412] LOG:')) 'the Postgres timestamp is kept'
    Assert-That ($all.Contains('2026-11-05 12:06:41.123456+00:00 [info] <0.1234.0>')) 'the RabbitMQ timestamp and process id are kept'
    Assert-That ($all.Contains('ref=ab12:cd34:ef56 sha=9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08')) 'hex IDs with colons and hashes are kept'
    Assert-That ($all.Contains('slot=3fa85f64-5717-4562-b3fc-2c963f66afa6 at 12:06:41')) 'a UUID and a plain time are kept'
    Assert-That ($all.Contains('user=procure_app database=procure_requests')) 'database role and database names are kept'
    Assert-That ($all.Contains("user 'svc-requests'")) 'a service account name is kept'

    # 6. pgaudit lines are severity INFO.
    $audit = @($records | Where-Object { $_.Body -match 'AUDIT: SESSION' })
    Assert-That ($audit.Count -eq 1) 'the pgaudit sample line came out'
    Assert-That (($audit | Where-Object { $_.Severity -eq 'INFO' }).Count -eq $audit.Count) 'pgaudit records have severity INFO'
    $others = @($records | Where-Object { $_.Body -notmatch 'AUDIT: SESSION' -and $_.Severity -eq 'INFO' })
    Assert-That ($others.Count -eq 0) 'other lines keep their (unset) severity'

    # 7. Nothing in the config uses a deprecated form: the collector's own log has no deprecation notice.
    $collectorLog = @(docker logs $name 2>&1 | ForEach-Object { "$_" })
    $deprecated = @($collectorLog | Where-Object { $_ -match '(?i)deprecat|paths were modified|context prefix' })
    if ($deprecated) { $deprecated | Select-Object -First 5 | ForEach-Object { Write-Host "  collector: $_" } }
    Assert-That ($collectorLog.Count -gt 0 -and $deprecated.Count -eq 0) "the collector log has no deprecation notice ($($collectorLog.Count) lines read)"
}
finally {
    docker rm -f $name 2>&1 | Out-Null
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All collector redaction checks passed.' -ForegroundColor Green
