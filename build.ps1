[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'MyPowerTools.slnx') -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

$template = Join-Path $toolRoot 'current-integration\modules\xbrd'
$packageRoot = Join-Path $toolRoot 'artifacts\package'

# The development overlay expects a materialized package (module.json + exactly one tool.json)
# before it builds the Surface and publishes the service units itself.
if (Test-Path -LiteralPath $packageRoot -PathType Container) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -Path (Join-Path $template '*') -Destination $packageRoot -Recurse -Force

$moduleManifest = Join-Path $packageRoot 'module.json'
if (-not (Test-Path -LiteralPath $moduleManifest -PathType Leaf)) {
    throw "Package template did not produce module.json: $moduleManifest"
}

$toolManifests = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter 'tool.json' |
    Where-Object {
        $manifest = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [string]$manifest.toolId -eq 'xbrd'
    })
if ($toolManifests.Count -ne 1) {
    throw "Expected exactly one tool.json with toolId 'xbrd' under the package, found $($toolManifests.Count)."
}

# --- Package contract checks (CONTRACT.md section 9 / t0 known pitfalls) ------
# These are cheap guards for exactly the mistakes that have already broken this
# tool once. They intentionally fail the build instead of shipping a package the
# Shell would silently degrade.
$toolManifestPath = $toolManifests[0].FullName
$toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw | ConvertFrom-Json
$toolDirectory = Split-Path -Parent $toolManifestPath

if ([string]$toolManifest.type -ne 'dotnet-surface') {
    throw "tool.json type must be 'dotnet-surface' so Shell loads the second (dotnet) route; found '$($toolManifest.type)'."
}

# module.json must declare at least one entrypoint (module.schema.json: minItems 1).
# For xbrd the honest declaration is the remote publisher HTTP facade; the Runner
# then health-checks {baseUrl}/health and can execute http.request palette commands.
$moduleDefinition = Get-Content -LiteralPath $moduleManifest -Raw | ConvertFrom-Json
$entrypoints = @($moduleDefinition.entrypoints)
if ($entrypoints.Count -eq 0) {
    throw 'module.json must declare at least one entrypoint (module.schema.json requires minItems 1).'
}
$httpEntrypoint = $entrypoints | Where-Object { [string]$_.kind -eq 'http' } | Select-Object -First 1
if ($null -eq $httpEntrypoint -or [string]::IsNullOrWhiteSpace([string]$httpEntrypoint.baseUrl)) {
    throw 'module.json must declare an http entrypoint with baseUrl for the remote publisher facade.'
}

$runtime = $toolManifest.runtime
if ($null -eq $runtime -or [string]$runtime.transport -ne 'remote-http') {
    throw "tool.json runtime.transport must be 'remote-http' (CONTRACT.md section 4)."
}
if ([string]::IsNullOrWhiteSpace([string]$runtime.endpoint)) {
    throw 'tool.json runtime.endpoint must not be empty.'
}

# Command ids ending in .refresh/.open-external are filtered out of web routes
# (ShellWorkspaceController.ExternalTools.cs:47-49), so they must never be declared.
foreach ($command in @($toolManifest.commands)) {
    $id = [string]$command.id
    if ($id -match '\.(refresh|open-external)$') {
        throw "Command id '$id' is filtered from web routes by Shell; rename it (CONTRACT.md section 4)."
    }
    if ([string]::IsNullOrWhiteSpace([string]$command.path)) {
        throw "Command '$id' has no path; remote-http commands are only reachable through the Shell HTTP path."
    }
}

# Every declared command must also be indexed (or replaced 1:1) by commands.index.json
# so it is reachable from the command palette through the module http entrypoint.
$indexPath = Join-Path $packageRoot 'commands.index.json'
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$indexedIds = @($index.commands | ForEach-Object { [string]$_.id })
foreach ($command in @($toolManifest.commands)) {
    if ($indexedIds -notcontains [string]$command.id) {
        throw "Command '$($command.id)' is declared in tool.json but missing from commands.index.json; the palette would not reach it."
    }
}

$panelRoute = @($toolManifest.routes | Where-Object { [string]$_.surface.kind -eq 'web' }) | Select-Object -First 1
if ($null -eq $panelRoute) {
    throw 'tool.json must declare one web route (the panel Tab).'
}
if ([string]::IsNullOrWhiteSpace([string]$panelRoute.surface.source)) {
    throw 'The web route must declare surface.source.'
}
if (@($panelRoute.surface.allowedOrigins).Count -eq 0) {
    throw 'The web route must declare allowedOrigins; the Shell refuses an embedded web surface without one.'
}

# Settings values/schema live at the module root; ui/settings.json is a UI surface
# declaration (kind=settings), not a values file. ui-surface.schema.json is enforced
# on every path listed in module.json uiSurfaces, so a values file placed there fails
# `mpt validate`.
$settingsSchema = [System.IO.Path]::GetFullPath((Join-Path $toolDirectory ([string]$toolManifest.settings.schema)))
$settingsValues = [System.IO.Path]::GetFullPath((Join-Path $toolDirectory ([string]$toolManifest.settings.values)))
foreach ($file in @($settingsSchema, $settingsValues)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Package is missing a settings file: $file"
    }
}
$settingsSurfacePath = Join-Path $packageRoot 'ui\settings.json'
$settingsSurface = Get-Content -LiteralPath $settingsSurfacePath -Raw | ConvertFrom-Json
if ([string]$settingsSurface.kind -ne 'settings') {
    throw "ui/settings.json must be a kind=settings UI surface declaration; found '$($settingsSurface.kind)'."
}
if (@($settingsSurface.states).Count -lt 3) {
    throw 'ui/settings.json must declare at least 3 states (ui-surface.schema.json).'
}

# Every ${settings.<key>} token used by the manifest must be present (and not
# empty) in the values file; ToolRegistry.ExpandSettings throws at module load
# time on an unresolved token, which would kill the whole module.
$values = Get-Content -LiteralPath $settingsValues -Raw | ConvertFrom-Json
$manifestText = Get-Content -LiteralPath $toolManifestPath -Raw
foreach ($match in [regex]::Matches($manifestText, '\$\{settings\.(?<name>[A-Za-z0-9_.-]+)\}')) {
    $name = $match.Groups['name'].Value
    $property = $values.PSObject.Properties[$name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "tool.json uses `${{settings.$name}} but the settings values file has no non-empty '$name'."
    }
}

Write-Host "xbrd package staged at $packageRoot" -ForegroundColor Green
Write-Host "  module.json : $moduleManifest"
Write-Host "  tool.json   : $toolManifestPath"
Write-Host "  configuration: $Configuration (used by the dev overlay / build-all-tools, not by this staging step)"
Write-Host "  routes      : $(@($toolManifest.routes | ForEach-Object { "$($_.routeId)[$($_.surface.kind)]" }) -join ', ')"
Write-Host "  commands    : $(@($toolManifest.commands | ForEach-Object { "$($_.id) $($_.method) $($_.path)" }) -join ' | ')"
Write-Host 'Next: pwsh -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd'
