<#
.SYNOPSIS
    Builds logiswitch and installs it to %LOCALAPPDATA%\Programs\logiswitch, then starts it.

.DESCRIPTION
    Run it again to update: it stops the running copy, publishes over it and starts the new one.
    The "Start with Windows" setting (tray menu) points at the installed exe, so it keeps working
    across updates. Requires the .NET 10 SDK.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\logiswitch'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

# Only one copy can run (single-instance), and a running exe can't be overwritten.
$running = Get-Process -Name logiswitch -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running logiswitch...'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Write-Host "Publishing to $Destination..."
dotnet publish (Join-Path $PSScriptRoot 'logiswitch.csproj') `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=embedded `
    -o $Destination
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $Destination 'logiswitch.exe'
if (-not $NoStart) {
    Write-Host "Starting $exe"
    Start-Process $exe
}
Write-Host 'Done. Use "Start with Windows" in the tray menu to launch it at sign-in.'
