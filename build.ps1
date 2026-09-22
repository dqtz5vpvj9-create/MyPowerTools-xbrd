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

Write-Host "xbrd package staged at $packageRoot" -ForegroundColor Green
Write-Host "  module.json : $moduleManifest"
Write-Host "  tool.json   : $($toolManifests[0].FullName)"
Write-Host 'Next: pwsh -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd'
