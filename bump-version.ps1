<#
.SYNOPSIS
  Bumps the app version (the <Version> in src\Mavyex\Mavyex.csproj – the only place it is stored;
  it ends up in the exe, the installer's "Apps & features" entry and the release file names).
.PARAMETER Part
  patch (default), minor or major.
.PARAMETER Version
  Set an explicit version instead, e.g. 1.2.0 or 1.2.0-beta.1
.PARAMETER DryRun
  Show what would change without writing anything.
.EXAMPLE
  .\bump-version.ps1            # 0.2.0 -> 0.2.1
  .\bump-version.ps1 minor      # 0.2.1 -> 0.3.0
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

$project = Join-Path $PSScriptRoot 'src\Mavyex\Mavyex.csproj'
$semver = '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$'
$text = [System.IO.File]::ReadAllText($project)
if ($text -notmatch '<Version>([^<]+)</Version>') { throw "<Version> not found in $project" }
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
if (-not $DryRun) {
    $text = $text.Replace("<Version>$current</Version>", "<Version>$new</Version>")
    [System.IO.File]::WriteAllText($project, $text, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host "  $(if ($DryRun) { 'would update' } else { 'updated' }) src\Mavyex\Mavyex.csproj"
Write-Host "Done. Next: .\release.ps1" -ForegroundColor Green
