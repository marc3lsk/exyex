<#
.SYNOPSIS
  Bumps the app version in every place it is stored.
.DESCRIPTION
  Updates Cargo.toml and the mavyex entry of Cargo.lock so they never drift apart
  (the version is baked into the exe via CARGO_PKG_VERSION and used by release.ps1).
.PARAMETER Part
  patch (default), minor or major.
.PARAMETER Version
  Set an explicit version instead, e.g. 1.2.0 or 1.2.0-beta.1
.PARAMETER DryRun
  Show what would change without writing anything.
.EXAMPLE
  .\bump-version.ps1            # 0.1.0 -> 0.1.1
  .\bump-version.ps1 minor      # 0.1.1 -> 0.2.0
  .\bump-version.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')][string]$Part = 'patch',
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$semver = '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text($path) { [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot $path)) }
function Write-Text($path, $text) {
    if (-not $DryRun) { [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot $path), $text, $utf8) }
}

# current version = the [package] version in Cargo.toml (source of truth)
$toml = Read-Text 'Cargo.toml'
if ($toml -notmatch '(?m)^version\s*=\s*"([^"]+)"') { throw 'version not found in Cargo.toml' }
$current = $Matches[1]

if ($Version) {
    if ($Version -notmatch $semver) { throw "'$Version' is not a valid semver (x.y.z[-pre])" }
    $new = $Version
} else {
    if ($current -notmatch $semver) { throw "Current version '$current' is not plain semver" }
    $maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]
    switch ($Part) {
        'major' { $maj++; $min = 0; $pat = 0 }
        'minor' { $min++; $pat = 0 }
        'patch' { $pat++ }
    }
    $new = "$maj.$min.$pat"
}
if ($new -eq $current) { Write-Host "Already at $current - nothing to do."; return }

Write-Host "Version: $current -> $new$(if ($DryRun) { '  (dry run)' })" -ForegroundColor Cyan

# Cargo.toml: only the FIRST top-level `version =` (the package one), never dependency versions
$rx = [regex]::new('(?m)^(version\s*=\s*")[^"]+(")')
Write-Text 'Cargo.toml' ($rx.Replace($toml, "`${1}$new`${2}", 1))
Write-Host "  $(if ($DryRun) { 'would update' } else { 'updated' }) Cargo.toml"

# Cargo.lock: only the mavyex package entry
if (Test-Path 'Cargo.lock') {
    $cl = Read-Text 'Cargo.lock'
    $rx = [regex]::new('(name = "mavyex"\r?\nversion = ")[^"]+(")')
    if ($rx.IsMatch($cl)) {
        Write-Text 'Cargo.lock' ($rx.Replace($cl, "`${1}$new`${2}", 1))
        Write-Host "  $(if ($DryRun) { 'would update' } else { 'updated' }) Cargo.lock"
    }
}

Write-Host "Done. Next: .\release.ps1" -ForegroundColor Green
