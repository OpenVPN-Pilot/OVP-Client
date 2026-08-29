<#
.SYNOPSIS
    Builds and runs the application from the source tree.

.DESCRIPTION
    Three things have to happen in order, and doing them by hand is how an old copy ends up being the
    one that is running: stop whatever is open, build, start what was just built. Only one copy runs
    per user, so starting a new one while an old one is open hands the request to the old one and
    changes nothing on screen.

    This does not publish and does not build an installer. Use installer/build.ps1 for that.

.PARAMETER Headless
    Start with no window and no notification area entry, the way something automated would.

.PARAMETER Connect
    Connect a profile once it has started. May be given more than once.

.PARAMETER NoBuild
    Start what is already built.

.PARAMETER Configuration
    Debug by default, because this is for working on it.

.EXAMPLE
    pwsh scripts/dev.ps1

.EXAMPLE
    pwsh scripts/dev.ps1 -Headless -Connect lab-01-cert
#>

[CmdletBinding()]
param(
    [switch] $Headless,
    [string[]] $Connect = @(),
    [switch] $NoBuild,
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$application = Join-Path $repository "src/OpenVpnPilot.App/bin/$Configuration/net10.0/OpenVpnPilot.exe"
$command = Join-Path $repository "src/OpenVpnPilot.Cli/bin/$Configuration/net10.0/ovp.exe"

# Whatever is open owns the profile store, and it is almost never the build about to be made.
$running = Get-Process -Name OpenVpnPilot -ErrorAction SilentlyContinue

if ($running) {
    Write-Host 'Stopping the copy that is running'

    if (Test-Path $command) {
        & $command stop | Out-Host
    }

    $waited = 0
    while ((Get-Process -Name OpenVpnPilot -ErrorAction SilentlyContinue) -and $waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
    }

    if (Get-Process -Name OpenVpnPilot -ErrorAction SilentlyContinue) {
        throw 'The copy that is running did not stop. Close it and run this again.'
    }
}

if (-not $NoBuild) {
    Write-Host "Building ($Configuration)"

    dotnet build (Join-Path $repository 'OpenVpnPilot.sln') --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}

if (-not (Test-Path $application)) {
    throw "Nothing to start: $application does not exist. Run without -NoBuild."
}

$arguments = @()

if ($Headless) {
    $arguments += '--headless'
}

foreach ($profile in $Connect) {
    $arguments += @('--connect', $profile)
}

Write-Host "Starting $application $($arguments -join ' ')"

# Started through the shell so it gets its own handles. Inheriting this terminal's would keep the
# prompt waiting for a window that is meant to outlive it. An empty argument list is not the same as
# no argument list, and Start-Process refuses the first, so the two cases are separate.
$directory = Split-Path -Parent $application

if ($arguments.Count -gt 0) {
    Start-Process -FilePath $application -ArgumentList $arguments -WorkingDirectory $directory
}
else {
    Start-Process -FilePath $application -WorkingDirectory $directory
}

Write-Host ''
Write-Host "The command is at $command"
Write-Host 'Add its directory to PATH for this session with:'
Write-Host "  `$env:PATH = '$(Split-Path -Parent $command);' + `$env:PATH"
