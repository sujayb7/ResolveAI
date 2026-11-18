<#
.SYNOPSIS
  Breaks a demo service "right now" so ResolveAI can investigate a fresh incident live, through its MCP tools.

.DESCRIPTION
  ResolveAI's MCP servers re-read log files, the deploy feed and metric series on every call. This script writes
  fresh, timestamped evidence for one scenario into demo-systems/live/ (git-ignored), so an incident created in the
  UI a minute later is investigated against data that did not exist when the app started.

  1) Once, before starting the app:   .\demo-systems\live-demo.ps1 -Setup
     (writes mcp.config.json so the live services, log folder, deploy feed and metrics are known; restart the app after)
  2) On stage, just before creating the incident:   .\demo-systems\live-demo.ps1 -Inject pinpoint-locale
     (prints the title, application name, description and impact to paste into "Investigate incident")
  3) Afterwards:   .\demo-systems\live-demo.ps1 -Reset

  Scenarios: pinpoint-locale, auth-keys
#>
[CmdletBinding(DefaultParameterSetName = 'Inject')]
param(
    [Parameter(ParameterSetName = 'Setup')][switch]$Setup,
    [Parameter(ParameterSetName = 'Inject', Position = 0)][ValidateSet('pinpoint-locale', 'auth-keys')][string]$Inject = 'pinpoint-locale',
    [Parameter(ParameterSetName = 'Reset')][switch]$Reset
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$live = Join-Path $PSScriptRoot 'live'
$config = Join-Path $repo 'mcp.config.json'
$backup = "$config.before-live-demo"
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Text([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}
function Iso([DateTime]$t) { $t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
function IsoS([DateTime]$t) { $t.ToString("yyyy-MM-ddTHH:mm:ssZ") }

# The services each scenario adds. Names must match the "Application name" typed in the UI.
$services = [ordered]@{
    'pinpoint' = @{ log_roots = @('demo-systems/live/logs/pinpoint'); key_metrics = @('Locale fallback downloads', 'Asset download requests') }
    'auth-api' = @{ log_roots = @('demo-systems/live/logs/auth-api'); key_metrics = @('HTTP 401 rate', 'Token validations') }
}

if ($Reset) {
    if (Test-Path $live) { Remove-Item -Recurse -Force $live }
    if (Test-Path $backup) { Move-Item -Force $backup $config; Write-Host "Restored your previous mcp.config.json." }
    elseif ((Test-Path $config) -and ((Get-Content $config -Raw) -match 'demo-systems/live')) { Remove-Item $config; Write-Host "Removed the live-demo mcp.config.json." }
    Write-Host "Live demo data removed. Restart ResolveAI to drop the live services."
    return
}

if ($Setup) {
    $source = if (Test-Path $config) { Copy-Item -Force $config $backup; $config } else { Join-Path $repo 'mcp.config.example.json' }
    $cfg = Get-Content $source -Raw | ConvertFrom-Json
    foreach ($name in $services.Keys) {
        if ($cfg.services.PSObject.Properties[$name]) { $cfg.services.PSObject.Properties.Remove($name) }
        $cfg.services | Add-Member -NotePropertyName $name -NotePropertyValue ([pscustomobject]$services[$name])
    }
    $roots = @($cfg.files.allowed_roots) + 'demo-systems/live/logs' | Select-Object -Unique
    $cfg.files.allowed_roots = @($roots)
    $cfg.deploys.feed = 'demo-systems/live/deploys.json'
    $cfg.metrics.series_dir = 'demo-systems/live/metrics'
    Write-Text $config ($cfg | ConvertTo-Json -Depth 10)

    # Start from the committed demo data so the seeded incidents keep working.
    New-Item -ItemType Directory -Force -Path (Join-Path $live 'metrics'), (Join-Path $live 'logs') | Out-Null
    Copy-Item -Force (Join-Path $PSScriptRoot 'deploys.json') (Join-Path $live 'deploys.json')
    Copy-Item -Force (Join-Path $PSScriptRoot 'metrics\*.json') (Join-Path $live 'metrics')
    foreach ($name in $services.Keys) { New-Item -ItemType Directory -Force -Path (Join-Path $live "logs\$name") | Out-Null }
    Write-Host "Live demo ready: mcp.config.json now knows $($services.Keys -join ', '). Restart ResolveAI, then run -Inject <scenario> on stage."
    return
}

if (-not (Test-Path (Join-Path $live 'deploys.json'))) { throw "Run '.\demo-systems\live-demo.ps1 -Setup' first (and restart ResolveAI)." }

$now = [DateTime]::UtcNow
function Add-Deploy([hashtable]$deploy) {
    $feedPath = Join-Path $live 'deploys.json'
    # Assign first: Windows PowerShell returns a JSON array as one object, and only a variable enumerates it reliably.
    $parsed = Get-Content $feedPath -Raw | ConvertFrom-Json
    $feed = @($parsed | Where-Object { $_.commit -ne $deploy.commit }) + [pscustomobject]$deploy
    Write-Text $feedPath (ConvertTo-Json -InputObject @($feed) -Depth 10)
}
function Write-Metrics([string]$service, [hashtable]$series) {
    # One point a minute for the last 40 minutes. Each series is (unit, normal value, value after the change, minutes ago it changed).
    $doc = [ordered]@{}
    foreach ($name in $series.Keys) {
        $unit, $normal, $after, $changedAgo = $series[$name]
        $points = for ($m = 40; $m -ge 0; $m--) {
            $v = if ($m -le $changedAgo) { $after } else { $normal }
            , @((IsoS $now.AddMinutes(-$m)), $v)
        }
        $doc[$name] = [ordered]@{ unit = $unit; points = @($points) }
    }
    Write-Text (Join-Path $live "metrics\$service.json") (ConvertTo-Json -InputObject $doc -Depth 10 -Compress)
}
function Write-Log([string]$service, [object[]]$lines) {
    # Each line: minutes ago, level, message. Written oldest first, like a real log.
    $text = ($lines | Sort-Object { - $_[0] } | ForEach-Object { "{0} {1,-5} {2}" -f (Iso $now.AddMinutes(-$_[0]).AddMilliseconds((Get-Random -Maximum 900))), $_[1], $_[2] }) -join "`n"
    Write-Text (Join-Path $live "logs\$service\app.log") ($text + "`n")
}

switch ($Inject) {
    'pinpoint-locale' {
        # Mirrors a real ticket shape: translated titles show, but every download except Japanese comes back in English.
        Add-Deploy @{
            service = 'pinpoint'; version = 'v7.3.0'; deployed_at = (IsoS $now.AddMinutes(-18)); commit = '4be91c2'; author = 'dev-content'
            change_summary = 'Topic asset downloads now resolve the file variant from the published_variants index (ja-JP backfilled; other locales pending backfill)'
            files_changed = @(@{ path = 'src/Assets/DownloadHandler.cs'; added = 22; removed = 9 }, @{ path = 'jobs/backfill-published-variants.sql'; added = 31; removed = 0 })
            patch = "--- a/src/Assets/DownloadHandler.cs`n+++ b/src/Assets/DownloadHandler.cs`n-  var file = assetStore.FindVariant(assetId, request.Locale) ?? assetStore.Default(assetId);`n+  var file = publishedVariants.Find(assetId, request.Locale) ?? assetStore.Default(assetId); // falls back to en-US`n"
        }
        Write-Log 'pinpoint' @(
            @(25, 'INFO', 'pinpoint asset download asset_id=8812 requested_locale=de-DE served_variant=de-DE'),
            @(19, 'INFO', 'deploy-agent pinpoint v7.3.0 rollout started (3/3 pods)'),
            @(17, 'INFO', 'pinpoint backfill-published-variants: 1,204 ja-JP variants indexed; de-DE, fr-FR, es-ES, zh-CN queued'),
            @(15, 'INFO', 'pinpoint asset download asset_id=8812 requested_locale=ja-JP served_variant=ja-JP'),
            @(14, 'WARN', 'pinpoint variant missing from published_variants; falling back to en-US asset_id=8812 requested_locale=de-DE'),
            @(12, 'WARN', 'pinpoint variant missing from published_variants; falling back to en-US asset_id=9034 requested_locale=fr-FR'),
            @(9, 'WARN', 'pinpoint variant missing from published_variants; falling back to en-US asset_id=8812 requested_locale=es-ES'),
            @(7, 'INFO', 'pinpoint asset download asset_id=9034 requested_locale=ja-JP served_variant=ja-JP'),
            @(5, 'WARN', 'pinpoint variant missing from published_variants; falling back to en-US asset_id=9120 requested_locale=zh-CN'),
            @(2, 'WARN', 'pinpoint variant missing from published_variants; falling back to en-US asset_id=8812 requested_locale=de-DE')
        )
        Write-Metrics 'pinpoint' @{ 'Locale fallback downloads' = @('per min', 0, 37, 16); 'Asset download requests' = @('per min', 120, 124, 16) }
        $form = [ordered]@{
            'Title' = 'Topic asset downloads return English files for non-Japanese participants'
            'Application name' = 'pinpoint'
            'Severity' = 'SEV-3'
            'Customer impact' = 'Participants in every language except Japanese download English Development Guide assets.'
            'Incident details' = "In Participant view all asset and topic translations are visible, but selecting an asset downloads the English file.`nJapanese participants get the correct Japanese file. The translated titles and descriptions display correctly.`nReported by the content team this afternoon; no content was changed in the admin tool."
        }
    }
    'auth-keys' {
        Add-Deploy @{
            service = 'auth-api'; version = 'v3.9.2'; deployed_at = (IsoS $now.AddMinutes(-14)); commit = 'd1a77e0'; author = 'dev-identity'
            change_summary = 'Rotate JWT signing key to kid 2026-11 and drop the previous key from the signer'
            files_changed = @(@{ path = 'config/signing-keys.yaml'; added = 3; removed = 6 })
            patch = "--- a/config/signing-keys.yaml`n+++ b/config/signing-keys.yaml`n-active_kid: 2026-10`n+active_kid: 2026-11`n-jwks_publish_previous: true`n+jwks_publish_previous: false`n"
        }
        Write-Log 'auth-api' @(
            @(20, 'INFO', 'auth-api jwks cache refreshed; keys=[2026-10] next_refresh_in=6h'),
            @(15, 'INFO', 'deploy-agent auth-api v3.9.2 rollout started (4/4 pods)'),
            @(13, 'INFO', 'auth-api signer active_kid=2026-11'),
            @(12, 'ERROR', 'auth-api token validation failed: kid 2026-11 not found in JWKS cache; status=401 route=/api/orders'),
            @(10, 'ERROR', 'auth-api token validation failed: kid 2026-11 not found in JWKS cache; status=401 route=/api/profile'),
            @(8, 'WARN', 'auth-api 401 rate above 20% for 5m on gateway-east'),
            @(4, 'ERROR', 'auth-api token validation failed: kid 2026-11 not found in JWKS cache; status=401 route=/api/checkout'),
            @(1, 'ERROR', 'auth-api token validation failed: kid 2026-11 not found in JWKS cache; status=401 route=/api/orders')
        )
        Write-Metrics 'auth-api' @{ 'HTTP 401 rate' = @('%', 0.4, 31, 12); 'Token validations' = @('per min', 5400, 5350, 12) }
        $form = [ordered]@{
            'Title' = 'Signed-in users suddenly getting logged out'
            'Application name' = 'auth-api'
            'Severity' = 'SEV-2'
            'Customer impact' = 'About a third of signed-in requests fail with 401; users are sent back to the login page.'
            'Incident details' = "Shortly after the afternoon release window users started being bounced to the login page.`nLogging in again works for a few seconds and then fails again. Both web and mobile are affected.`nNo infrastructure alerts apart from the gateway 401 rate."
        }
    }
}

Write-Host ""
Write-Host "Injected '$Inject' at $(IsoS $now). Now open Investigate incident and enter:" -ForegroundColor Green
foreach ($k in $form.Keys) { Write-Host ""; Write-Host "${k}:" -ForegroundColor Cyan; Write-Host $form[$k] }
