<#
.SYNOPSIS
    Builds the portable ImmersiveSBS folder, and a zip of it, that a download or an installer ships.

.DESCRIPTION
    A self-contained .NET 10 publish for win-x64 (the user needs no .NET installed), ready to run
    (ReadyToRun), with the browser extension, the licence, the third-party notices and their
    licence files, and the depth models asked for, taken from this PC's models folder. The app
    looks for models in its own models folder first, then in %LOCALAPPDATA%\ImmersiveSBS\models.

    Output: artifacts\ImmersiveSBS-<version>-win-x64\ and, unless -NoZip, the same name .zip.
    The version is the project's <Version>.

.PARAMETER Models
    Model families to ship, all their sizes: dad-base (the default model), da2-small, da3-small,
    da3-base, vda-small. None by default: the user then adds models themselves.

.PARAMETER ModelSource
    Where the model files are taken from. Default: %LOCALAPPDATA%\ImmersiveSBS\models.

.PARAMETER NoZip
    Leave the folder only.

.EXAMPLE
    .\packaging\publish.ps1 -Models dad-base
#>
param(
    [string[]] $Models = @(),
    [string] $ModelSource = (Join-Path $env:LOCALAPPDATA 'ImmersiveSBS\models'),
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ImmersiveSBS\ImmersiveSBS.csproj'

# Only models whose licence allows shipping (THIRD-PARTY-NOTICES.md).
$shippable = @('dad-base', 'da2-small', 'da3-small', 'da3-base', 'vda-small')
foreach ($m in $Models) {
    if ($shippable -notcontains $m) { throw "Model '$m' is not one to ship. Ship any of: $($shippable -join ', ')." }
}

[xml] $xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> in $project." }
$name = "ImmersiveSBS-$version-win-x64"
$out = Join-Path $root "artifacts\$name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "Publishing ImmersiveSBS $version to $out"
# Its own build folders: a clean build, and one a running copy of the app (bin\Release) cannot lock.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    --artifacts-path (Join-Path $root 'artifacts\build') -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $out 'extension\manifest.json'))) { throw 'The browser extension is missing from the published folder.' }

# The documents a user and a licence need.
Copy-Item (Join-Path $root 'LICENSE') $out
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out
Copy-Item (Join-Path $root 'README.md') $out
$licenses = New-Item -ItemType Directory -Force (Join-Path $out 'licenses')
$packages = Join-Path $env:USERPROFILE '.nuget\packages'
$onnx = Join-Path $packages 'microsoft.ml.onnxruntime\1.30.0'
Copy-Item (Join-Path $onnx 'LICENSE') (Join-Path $licenses 'onnxruntime-LICENSE.txt')
Copy-Item (Join-Path $onnx 'ThirdPartyNotices.txt') (Join-Path $licenses 'onnxruntime-ThirdPartyNotices.txt')

# Debug symbols stay out of the package.
Get-ChildItem $out -Filter '*.pdb' | Remove-Item

# The models asked for, every size of each.
if ($Models.Count -gt 0) {
    $modelsOut = New-Item -ItemType Directory -Force (Join-Path $out 'models')
    foreach ($m in $Models) {
        $files = @(Get-ChildItem $ModelSource -File | Where-Object { $_.Name -like "${m}_*.onnx" -or $_.Name -like "${m}-first_*.onnx" })
        if ($files.Count -eq 0) { throw "No $m model files in $ModelSource." }
        foreach ($f in $files) {
            Copy-Item $f.FullName $modelsOut
            Write-Host ("  model {0} ({1:N0} MB)" -f $f.Name, ($f.Length / 1MB))
        }
    }
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Folder: {0} ({1:N0} MB)" -f $out, ($size / 1MB))

if (-not $NoZip) {
    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("Zip: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
