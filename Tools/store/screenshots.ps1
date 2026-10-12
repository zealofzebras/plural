<#
.SYNOPSIS
  Renders store screenshots of the real Plural UI with simulated mics, into Marketing/screenshots/<profile>/.
.DESCRIPTION
  Runs Src/Plural.Screenshots (headless Avalonia): three replayed SmartMike+ sessions, synthetic local mics and two
  simulated remote devices, driven through the scenes in Marketing/<locale>/captions.txt order.
  With -Preview it also builds the framed store images into out/store (see Marketing/README.md).
.EXAMPLE
  ./Tools/store/screenshots.ps1                    # all profiles: phone, tablet, desktop
  ./Tools/store/screenshots.ps1 -Profile phone -Preview
#>
param(
    [ValidateSet('all', 'phone', 'tablet', 'desktop')] [string] $Profile = 'all',
    [ValidateSet('dark', 'light')] [string] $Theme = 'dark',
    [string] $Out,
    [switch] $Preview
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Out) { $Out = Join-Path $root 'Marketing\screenshots' }

# The simulated SmartMikes decode real audio, so the CELT library for this machine must exist.
$rid = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
if ($IsWindows -and -not (Test-Path (Join-Path $root "Native\celt\runtimes\$rid\native\celt071.dll"))) {
    throw "Native/celt/runtimes/$rid is missing: build it first with 'bash Native/celt/build-windows.sh'."
}

dotnet run --project (Join-Path $root 'Src\Plural.Screenshots') -c Release -- --profile $Profile --theme $Theme --out $Out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($Preview) {
    python (Join-Path $root 'Tools\store\store_assets.py') build --version 0.0.0 --build-number 1 --out (Join-Path $root 'out\store')
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Framed store images: $(Join-Path $root 'out\store')"
}
