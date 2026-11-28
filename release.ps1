<#
.SYNOPSIS
  Publishes mavyex.exe (single file) and collects it in .\release\ (portable .exe, setup .exe and .zip).
.PARAMETER Bundle
  exe (portable), setup (self-installing exe: per-user install, adds mavyex to PATH, Start menu), zip or all (default).
.PARAMETER SelfContained
  Bundle the .NET runtime into the exe (~70 MB, runs without .NET installed). Default: framework-dependent
  (small exe, needs the .NET 10 Desktop Runtime).
.PARAMETER SkipTests
  Skip `dotnet test` before publishing.
.EXAMPLE
  .\release.ps1
  .\release.ps1 -Bundle setup -SelfContained
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'exe', 'setup', 'zip')][string]$Bundle = 'all',
    [switch]$SelfContained,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "'dotnet' not found. Install the .NET 10 SDK." }

function Invoke-Step([string]$title, [scriptblock]$block) {
    Write-Host "> $title" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $title (exit code $LASTEXITCODE)" }
}

$project = 'src\Mavyex\Mavyex.csproj'
$ver = if ((Get-Content $project -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { throw "Version not found in $project" }
Write-Host "Packaging mavyex $ver$(if ($SelfContained) { ' (self-contained)' })" -ForegroundColor Green

if (-not $SkipTests) {
    Invoke-Step 'dotnet test' { dotnet test mavyex.slnx -c Release -nologo -v q }
}

$pub = Join-Path $PSScriptRoot 'release\publish'
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
$sc = if ($SelfContained) { 'true' } else { 'false' }
Invoke-Step 'dotnet publish' {
    dotnet publish $project -c Release -r win-x64 --self-contained $sc -o $pub -nologo -v q `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$sc -p:DebugType=none
}

$built = Join-Path $pub 'mavyex.exe'
if (-not (Test-Path $built)) { throw "Publish output not found: $built" }
$out = Join-Path $PSScriptRoot 'release'
$produced = @()
if ($Bundle -in 'all', 'exe') {
    $produced += Copy-Item $built (Join-Path $out "mavyex_${ver}_x64.exe") -Force -PassThru
}
if ($Bundle -in 'all', 'setup') {
    # Same binary under a *_setup name: mavyex detects that and runs the install wizard.
    $produced += Copy-Item $built (Join-Path $out "mavyex_${ver}_x64_setup.exe") -Force -PassThru
}
if ($Bundle -in 'all', 'zip') {
    $zip = Join-Path $out "mavyex_${ver}_x64.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    $items = @($built); if (Test-Path README.md) { $items += (Resolve-Path README.md).Path }
    Compress-Archive -Path $items -DestinationPath $zip
    $produced += Get-Item $zip
}

Write-Host "`nArtifacts:" -ForegroundColor Green
foreach ($f in $produced) {
    $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    '{0,-30} {1,7:N1} MB  sha256 {2}' -f $f.Name, ($f.Length / 1MB), $hash.Substring(0, 16)
}
Write-Host "`nCopied to $out" -ForegroundColor Green
