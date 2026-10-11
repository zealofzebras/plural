<#
.SYNOPSIS
  Packs a published Plural.App.Desktop folder into an .msix (one architecture).
.EXAMPLE
  ./build-msix.ps1 -PublishDir out/publish/win-x64 -Arch x64 -Version 1.2.3.0 -AssetsDir out/store/msix-assets `
                   -OutFile out/msix/Plural_1.2.3.0_x64.msix -IdentityName Plural -Publisher "CN=Plural" -PublisherDisplayName Plural
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+\.0$')] [string] $Version,  # Store requires revision 0
    [Parameter(Mandatory)] [string] $AssetsDir,
    [Parameter(Mandatory)] [string] $OutFile,
    [Parameter(Mandatory)] [string] $IdentityName,
    [Parameter(Mandatory)] [string] $Publisher,
    [Parameter(Mandatory)] [string] $PublisherDisplayName,
    [string] $DisplayName = 'Plural',
    [string] $Description = 'Record several microphones at once, in sync.'
)
$ErrorActionPreference = 'Stop'

function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $kits -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $tool) { throw "$name not found under $kits (install the Windows SDK)" }
    $tool
}
function Escape-Xml([string] $s) { [System.Security.SecurityElement]::Escape($s) }

$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) "plural-msix-$Arch-$([guid]::NewGuid().ToString('N'))"
$layout = Join-Path $work 'layout'
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null

Copy-Item (Join-Path $PublishDir '*') $layout -Recurse
# The app only needs its own architecture's native libraries.
Get-ChildItem (Join-Path $layout 'runtimes') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "win-$Arch" } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $AssetsDir '*.png') (Join-Path $layout 'Assets')

$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$tokens = @{
    IDENTITY_NAME = $IdentityName; PUBLISHER = $Publisher; PUBLISHER_DISPLAY_NAME = $PublisherDisplayName
    VERSION = $Version; ARCH = $Arch; DISPLAY_NAME = $DisplayName; DESCRIPTION = $Description
}
foreach ($k in $tokens.Keys) { $manifest = $manifest.Replace("{{$k}}", (Escape-Xml $tokens[$k])) }
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8NoBOM

# resources.pri maps Assets\X.png to the scale/targetsize-qualified files.
function Invoke-Tool([string] $tool, [string[]] $toolArgs) {
    $log = & $tool @toolArgs 2>&1
    if ($LASTEXITCODE) { $log | Out-Host; throw "$(Split-Path $tool -Leaf) failed ($LASTEXITCODE)" }
}
$priConfig = Join-Path $work 'priconfig.xml'
Invoke-Tool $makepri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o')
# One resources.pri in the package (no per-scale resource packages).
$cfg = [xml](Get-Content $priConfig -Raw)
$packaging = $cfg.SelectSingleNode('/resources/packaging')
if ($packaging) { [void]$packaging.ParentNode.RemoveChild($packaging); $cfg.Save($priConfig) }
Invoke-Tool $makepri @('new', '/pr', $layout, '/cf', $priConfig, '/mn', (Join-Path $layout 'AppxManifest.xml'),
                       '/of', (Join-Path $layout 'resources.pri'), '/o')

New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
Invoke-Tool $makeappx @('pack', '/d', $layout, '/p', $OutFile, '/o')
Remove-Item $work -Recurse -Force
Write-Host "built $OutFile"
