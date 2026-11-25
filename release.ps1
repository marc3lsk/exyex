<#
.SYNOPSIS
  Builds the optimized mavyex.exe and collects it in .\release\ (portable .exe and .zip).
.PARAMETER Bundle
  exe (portable), setup (self-installing exe: adds mavyex to PATH), zip or all (default).
.PARAMETER SkipChecks
  Skip `cargo check` and `cargo test` before building.
.EXAMPLE
  .\release.ps1
  .\release.ps1 -Bundle zip
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'exe', 'setup', 'zip')][string]$Bundle = 'all',
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if ((Test-Path $cargoBin) -and ($env:Path -notlike "*$cargoBin*")) { $env:Path += ";$cargoBin" }

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw "'cargo' not found. Install Rust from https://rustup.rs" }

function Invoke-Step([string]$title, [scriptblock]$block) {
    Write-Host "> $title" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $title (exit code $LASTEXITCODE)" }
}

# --- Cargo.toml and Cargo.lock must agree
$toml = Get-Content Cargo.toml -Raw
$ver = if ($toml -match '(?m)^version\s*=\s*"([^"]+)"') { $Matches[1] } else { throw 'version not found in Cargo.toml' }
if (Test-Path Cargo.lock) {
    $lockVer = if ((Get-Content Cargo.lock -Raw) -match 'name = "mavyex"\r?\nversion = "([^"]+)"') { $Matches[1] } else { $null }
    if ($lockVer -and $lockVer -ne $ver) {
        throw "Version mismatch: Cargo.toml=$ver, Cargo.lock=$lockVer. Run .\bump-version.ps1 -Version $ver"
    }
}
Write-Host "Packaging mavyex $ver" -ForegroundColor Green

# a running instance locks the exe
Get-Process mavyex -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Warning "mavyex is running (PID $($_.Id)) - stopping it."
    Stop-Process -Id $_.Id -Force
}

if (-not $SkipChecks) {
    Invoke-Step 'cargo check'  { cargo check --release }
    Invoke-Step 'cargo test'   { cargo test --release }
}
Invoke-Step 'cargo build --release' { cargo build --release }

# --- collect artifacts
$built = Join-Path $PSScriptRoot 'target\release\mavyex.exe'
if (-not (Test-Path $built)) { throw "Build output not found: $built" }
$out = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Force $out | Out-Null

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
