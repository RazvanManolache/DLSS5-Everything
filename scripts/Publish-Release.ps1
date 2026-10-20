#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Tag = 'v3.1.0-preview.1',
    [ValidatePattern('^app(?:-[a-z0-9]+)?$')][string]$PackageDirectory = 'app'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Use a version tag such as v3.0.0-preview.1.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseRoot = Join-Path $projectRoot "local-release/$Tag"
$packageRoot = Join-Path $releaseRoot $PackageDirectory
$assetsRoot = Join-Path $releaseRoot 'assets'

# Publishing must fail rather than silently omit one of the advertised inputs.
# Build native helpers and the Windows codec first. The Android companion is
# maintained separately and must never be included in this distribution.
$requiredFiles = @(
    'runtime/host64/dlss5-feed-host64.exe', 'runtime/host64/phoenix-vulkan-feed.addon64',
    'runtime/x86-dx9-dx11/dlss5-feed.addon32', 'runtime/x64-dx9-dx11/dlss5-feed.addon64',
    'runtime/openvr/dlss5-openvr-shim64.dll', 'runtime/openxr/XR_APILAYER_DLSS5_everything.dll',
    'runtime/screen/dlss5-screen.exe', 'runtime/codec/dlss5-pyrowave.exe',
    'runtime/codec/phoenix-pyrowave.dll',
    'runtime/software-reshade/ReShadeSoftware32.dll', 'runtime/software-reshade/ReShadeSoftware64.dll',
    'runtime/software-reshade/LICENSE.md', 'runtime/software-reshade/DEPENDENCIES.txt',
    'runtime/software-reshade/BUILD.json'
)
foreach ($relative in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) { throw "Build required release input first: $relative" }
}
if (Test-Path -LiteralPath $packageRoot) { throw "Package output already exists. Preserve or remove that specific output before republishing: $packageRoot" }
[void][IO.Directory]::CreateDirectory($assetsRoot)
Push-Location $projectRoot
try {
    & dotnet publish app/Dlss5CompatApp/Dlss5CompatApp.csproj -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -o $packageRoot --nologo
    if ($LASTEXITCODE) { throw 'Self-contained app publish failed.' }
    Copy-Item -LiteralPath README.md, LICENSE, THIRD_PARTY_NOTICES.md -Destination $packageRoot
    $docsRoot = Join-Path $packageRoot 'docs'
    [void][IO.Directory]::CreateDirectory($docsRoot)
    foreach ($entry in Get-ChildItem -LiteralPath docs) {
        Copy-Item -LiteralPath $entry.FullName -Destination $docsRoot -Recurse -Force
    }
    & (Join-Path $PSScriptRoot 'Copy-DistributionNotices.ps1') -PackageRoot $packageRoot

    # Neither machine preferences nor externally supplied models/runtimes belong
    # in the archive. Keep this a strict allow-by-build package, not a workspace ZIP.
    $forbidden = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object {
        $_.Name -match '^(?:nvngx.*\.dll|PhoenixModel\.dll|Phoenix\.addon(?:32|64)|ReShade(?:32|64)\.dll|camera-studio\.json|screen-studio\.json|pairing\.json)$' -or
        $_.Extension -in '.pdb', '.trx', '.log', '.jks', '.keystore', '.apk', '.java' -or
        $_.FullName -match '[\\/](?:Payload|ScreenSessions|LanSessions|TestResults|Android)[\\/]'
    })
    if ($forbidden.Count) { throw ('Unexpected private/external files in package: ' + (($forbidden | ForEach-Object Name) -join ', ')) }
    $inventory = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'FILES_SHA256.json') -Encoding utf8
    $archive = Join-Path $assetsRoot "DLSS5-Everything-$Tag-win-x64.zip"
    if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive" }
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $checksums = @(Get-ChildItem -LiteralPath $assetsRoot -File | Where-Object Extension -eq '.zip' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    })
    $checksums | Set-Content -LiteralPath (Join-Path $assetsRoot 'SHA256SUMS.txt') -Encoding utf8
    Write-Host "Release assets prepared in $assetsRoot. This script does not publish to GitHub."
} finally { Pop-Location }
