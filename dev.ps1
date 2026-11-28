<#
.SYNOPSIS
  Builds and starts mavyex in development mode.
.DESCRIPTION
  Default: debug build + run.
  -Watch   : rebuild and restart on every source change (dotnet watch).
  -DataDir : use this data folder instead of %APPDATA%\mavyex (sets MAVYEX_HOME). A test instance with its
             own folder runs next to the installed mavyex (separate single-instance mutex and pipe).
  MAVYEX_SOFTWARE_RENDER=1 makes PrintWindow screenshots work (WPF hardware rendering captures black).
  Without -DataDir every running mavyex is stopped first (one instance per user).
  Extra arguments after -- are passed to mavyex, e.g. --background.
.EXAMPLE
  .\dev.ps1
  .\dev.ps1 -Watch
  .\dev.ps1 -DataDir $env:TEMP\qtest
#>
[CmdletBinding()]
param(
    [switch]$Watch,
    [string]$DataDir,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "'dotnet' not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10"
}

$project = 'src\Mavyex\Mavyex.csproj'
$appArgs = @($AppArgs | Where-Object { $_ -ne '--' })

if ($DataDir) {
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    $env:MAVYEX_HOME = (Resolve-Path $DataDir).Path
    Write-Host "> data folder: $env:MAVYEX_HOME" -ForegroundColor Cyan
} else {
    Remove-Item Env:MAVYEX_HOME -ErrorAction SilentlyContinue
    # one instance per user: a second start would only show the running one
    Get-Process mavyex -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Warning "mavyex is already running (PID $($_.Id), $($_.Path)) - stopping it."
        Stop-Process -Id $_.Id -Force
    }
}

if ($Watch) {
    Write-Host '> dotnet watch (Ctrl+C to stop)' -ForegroundColor Cyan
    dotnet watch --project $project run -- @appArgs
} else {
    Write-Host '> dotnet run' -ForegroundColor Cyan
    dotnet run --project $project -- @appArgs
}
exit $LASTEXITCODE
