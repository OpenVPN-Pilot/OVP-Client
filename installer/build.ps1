<#
.SYNOPSIS
    Builds the Windows installer.

.DESCRIPTION
    Publishes the application and the ovp command into one directory and packages it as an MSI.
    Both go into the same directory on purpose: they share their dependencies, and one directory on
    PATH is what makes ovp work in any terminal.

    The installer is not part of the solution. Adding it would put the WiX toolset between a
    developer and an ordinary build, and building an installer is not something an ordinary build
    should do.

.PARAMETER SelfContained
    Carries the .NET runtime, so the installed application has no prerequisite beyond OpenVPN
    itself. Turn it off for a much smaller package that needs the .NET desktop runtime installed.

.EXAMPLE
    pwsh installer/build.ps1
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [bool] $SelfContained = $true,
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repository 'artifacts/install'
$outputDirectory = Join-Path $repository 'artifacts/release'

if (-not $Version) {
    $properties = Join-Path $repository 'Directory.Build.props'
    $document = [xml](Get-Content -Raw -Path $properties)
    $node = $document.SelectSingleNode('/Project/PropertyGroup/Version')

    if ($node) {
        $Version = $node.InnerText
    }
}

if (-not $Version) {
    throw 'No version was given and none could be read from Directory.Build.props.'
}

# An installer version is three or four numbers and nothing else. A tag such as 0.2.0-beta names a
# release the format cannot express, so the numbers are used and the rest is said out loud rather
# than dropped quietly.
$productVersion = [regex]::Match($Version, '^\d+(\.\d+){0,3}').Value

if (-not $productVersion) {
    throw "The version '$Version' does not begin with a number, so it cannot become an installer version."
}

if ($productVersion -ne $Version) {
    Write-Warning "The installer is versioned $productVersion; an installer version cannot carry '$Version'."
}

Write-Host "Building OpenVpnPilot $Version ($Configuration, $Runtime)"

# The WiX toolset is a global tool rather than a package reference, so it is checked for by name
# instead of being restored with the solution.
if (-not (Get-Command 'wix' -ErrorAction SilentlyContinue)) {
    throw 'The WiX toolset is missing. Install it with: dotnet tool install --global wix'
}

# The extension has to match the toolset exactly. Left to resolve on its own it picks the newest
# published version, which is not necessarily one this toolset can load.
$wixVersion = (wix --version) -replace '\+.*$', ''

if (-not (wix extension list --global | Select-String -SimpleMatch 'WixToolset.UI.wixext')) {
    Write-Host "Adding the WiX user interface extension $wixVersion"
    wix extension add --global "WixToolset.UI.wixext/$wixVersion"
    if ($LASTEXITCODE -ne 0) { throw 'The WiX user interface extension could not be added.' }
}

# A previous publish is cleared rather than published over, so a file that is no longer part of the
# application cannot end up inside the installer. That fails if the application is running from
# there, which is easy to do and produces an error naming a single locked DLL and nothing useful.
if (Test-Path $publishDirectory) {
    $holders = Get-Process |
        Where-Object {
            try { $_.Path -and $_.Path.StartsWith($publishDirectory, [StringComparison]::OrdinalIgnoreCase) }
            catch { $false }
        }

    if ($holders) {
        $names = ($holders | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
        throw "The publish directory is in use by $names. Close it and run this again, for example with: ovp stop"
    }

    Remove-Item -Recurse -Force $publishDirectory
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

foreach ($project in @('src/OpenVpnPilot.App', 'src/OpenVpnPilot.Cli')) {
    Write-Host "Publishing $project"

    dotnet publish (Join-Path $repository $project) `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $SelfContained.ToString().ToLowerInvariant() `
        --output $publishDirectory

    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

# A publish leaves the debugging symbols behind, which double the size of the package and are of no
# use on a machine that only runs the application.
Get-ChildItem -Path $publishDirectory -Filter '*.pdb' -Recurse | Remove-Item -Force

$package = Join-Path $outputDirectory "OpenVpnPilot-$Version-$Runtime.msi"
$source = Join-Path $PSScriptRoot 'OpenVpnPilot.wxs'
$license = Join-Path $PSScriptRoot 'License.rtf'

Write-Host "Packaging $package"

# Every value is expanded into a string first. An unquoted expression would be passed as a separate
# argument, and a path with a space in it would be split in two.
wix build "$source" `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -define "ProductVersion=$productVersion" `
    -define "PublishDir=$publishDirectory" `
    -define "LicenseFile=$license" `
    -out "$package"

if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built.' }

Write-Host ''
Write-Host "Wrote $package"
Write-Host 'Install it with: msiexec /i "<path>"   Remove it from Apps and features, or with msiexec /x.'
