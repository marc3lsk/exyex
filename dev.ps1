<#
.SYNOPSIS
  Starts exyex in development mode.
.DESCRIPTION
  Default: debug build + run (fast compile, slower runtime).
  -Release : optimized build + run (use when judging real-world speed of big folders).
  -Watch   : rebuild and restart on every source change (needs cargo-watch, offered to be installed).
  Extra arguments after -- are passed to exyex, e.g. a folder to open.
.EXAMPLE
  .\dev.ps1
  .\dev.ps1 -Watch
  .\dev.ps1 -Release -- C:\Users
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Watch,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# rustup installs to %USERPROFILE%\.cargo\bin, which a fresh shell may not have on PATH yet
$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if ((Test-Path $cargoBin) -and ($env:Path -notlike "*$cargoBin*")) { $env:Path += ";$cargoBin" }

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "'cargo' not found. Install Rust from https://rustup.rs (plus VS Build Tools, `"Desktop development with C++`")."
}

# a running instance locks target\...\exyex.exe and breaks the rebuild
Get-Process exyex -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Warning "exyex is already running (PID $($_.Id)) - stopping it."
    Stop-Process -Id $_.Id -Force
}

$profileArgs = @(); if ($Release) { $profileArgs += '--release' }
$appArgs = @($AppArgs | Where-Object { $_ -ne '--' })

if ($Watch) {
    if (-not (Get-Command cargo-watch -ErrorAction SilentlyContinue)) {
        Write-Host '> cargo install cargo-watch (one-time)' -ForegroundColor Cyan
        cargo install cargo-watch
        if ($LASTEXITCODE -ne 0) { throw 'cargo install cargo-watch failed' }
    }
    Write-Host '> cargo watch (Ctrl+C to stop)' -ForegroundColor Cyan
    $run = ('run ' + ($profileArgs -join ' ') + $(if ($appArgs) { ' -- ' + ($appArgs -join ' ') } else { '' })).Trim()
    cargo watch -q -c -w src -w Cargo.toml -x $run
} else {
    Write-Host "> cargo run$(if ($Release) { ' --release (first build takes a few minutes)' })" -ForegroundColor Cyan
    if ($appArgs) { cargo run @profileArgs -- @appArgs } else { cargo run @profileArgs }
}
exit $LASTEXITCODE
