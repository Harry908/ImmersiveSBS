<#
.SYNOPSIS
    Builds the one-click installer, artifacts\ImmersiveSBS-Setup-<version>.exe.

.DESCRIPTION
    1. publish.ps1 makes the self-contained app folder with the models asked for.
    2. The Virtual Display Driver 25.7.23 package is downloaded once into artifacts\cache and
       checked against its SHA-256; its 64-bit driver files, devcon.exe and the certificate of
       its publisher (taken from the driver's signed catalog) are staged for the installer.
    3. Inno Setup 6 compiles packaging\ImmersiveSBS.iss.

    Inno Setup is found in artifacts\tools\InnoSetup (a portable copy: run the official
    installer with /PORTABLE=1 /CURRENTUSER /DIR=<that folder>) or in Program Files, or passed
    with -Iscc.

.PARAMETER Models
    Model families to ship; Distill-Any-Depth Base (the default model) by default.

.PARAMETER Iscc
    The path of ISCC.exe, if not in one of the usual places.

.EXAMPLE
    .\packaging\build-installer.ps1
#>
param(
    [string[]] $Models = @('dad-base'),
    [string] $Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

# The driver release the installer ships, pinned by its hash.
$vddVersion = '25.7.23'
$vddUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/$vddVersion/VDD.Control.$vddVersion.zip"
$vddSha256 = 'a701f2272e9fcf382849b24f913c6dd07597b3b1116525f2e90182f019609154'

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path $artifacts 'tools\InnoSetup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found; see this script''s help.' }

# 1. The app.
& (Join-Path $PSScriptRoot 'publish.ps1') -Models $Models -NoZip
[xml] $xml = Get-Content (Join-Path $root 'src\ImmersiveSBS\ImmersiveSBS.csproj')
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$appDir = Join-Path $artifacts "ImmersiveSBS-$version-win-x64"

# 2. The driver.
$cache = New-Item -ItemType Directory -Force (Join-Path $artifacts 'cache')
$zip = Join-Path $cache "VDD.Control.$vddVersion.zip"
if (-not (Test-Path $zip)) {
    Write-Host "Downloading the Virtual Display Driver $vddVersion"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $vddUrl -OutFile $zip
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $vddSha256) { Remove-Item $zip; throw "The driver package's SHA-256 is $hash, not $vddSha256; deleted it." }
$unpacked = Join-Path $cache "VDD.Control.$vddVersion"
if (-not (Test-Path $unpacked)) { Expand-Archive $zip $unpacked }

$driverDir = Join-Path $artifacts 'driver'
if (Test-Path $driverDir) { Remove-Item -Recurse -Force $driverDir }
New-Item -ItemType Directory $driverDir | Out-Null
# The package's "x86" folder holds the amd64 driver.
foreach ($f in 'MttVDD.inf', 'mttvdd.cat', 'MttVDD.dll') { Copy-Item (Join-Path $unpacked "SignedDrivers\x86\VDD\$f") $driverDir }
Copy-Item (Join-Path $unpacked 'Dependencies\devcon.exe') $driverDir
Copy-Item (Join-Path $root 'packaging\vdd\LICENSE.txt') (Join-Path $driverDir 'LICENSE-VirtualDisplayDriver.txt')
foreach ($f in 'mttvdd.cat', 'MttVDD.dll', 'devcon.exe') {
    $s = Get-AuthenticodeSignature (Join-Path $driverDir $f)
    if ($s.Status -ne 'Valid') { throw "$f is not validly signed ($($s.Status))." }
}
$signer = (Get-AuthenticodeSignature (Join-Path $driverDir 'mttvdd.cat')).SignerCertificate
[System.IO.File]::WriteAllBytes((Join-Path $driverDir 'signpath.cer'), $signer.Export('Cert'))
Write-Host "Driver signed by $($signer.Subject), thumbprint $($signer.Thumbprint), valid to $($signer.NotAfter.ToString('yyyy-MM-dd'))"

# 3. The installer.
& $Iscc /Qp "/DAppVersion=$version" "/DAppDir=$appDir" "/DDriverDir=$driverDir" "/DRepoDir=$root" `
    "/DOutputDir=$artifacts" "/DSignPathThumbprint=$($signer.Thumbprint)" (Join-Path $PSScriptRoot 'ImmersiveSBS.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }
$setup = Get-Item (Join-Path $artifacts "ImmersiveSBS-Setup-$version.exe")
Write-Host ("Installer: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB))
