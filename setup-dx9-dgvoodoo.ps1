[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [Parameter(Mandatory = $true)]
    [string]$DgVoodooPath,

    [ValidateSet("x86", "x64")]
    [string]$Arch = "x86",

    [ValidateSet("d3d11_fl10_0", "d3d11_fl10_1", "d3d11_fl11_0", "d3d11_fl12_0")]
    [string]$OutputApi = "d3d11_fl11_0",

    [ValidateRange(128, 1792)]
    [int]$VRAM = 1024,

    [switch]$NoWatermark,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

function Find-DgVoodooFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$RequiredArch = ""
    )

    $matches = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Name |
        Where-Object {
            if ($RequiredArch -eq "") { return $true }
            $normalized = $_.FullName.Replace("/", "\")
            return $normalized -like "*\MS\$RequiredArch\$Name"
        }

    if (-not $matches) {
        if ($RequiredArch -ne "") {
            throw "Could not find MS\$RequiredArch\$Name under '$Root'."
        }
        throw "Could not find $Name under '$Root'."
    }

    return $matches[0].FullName
}

function Copy-WithBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$BackupDir,
        [switch]$Force
    )

    if (Test-Path -LiteralPath $Destination) {
        $same = $false
        try {
            $same = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -eq
                    (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        } catch {
            $same = $false
        }

        if ($same) {
            return "already current: $Destination"
        }

        if (-not $Force) {
            throw "'$Destination' already exists. Re-run with -Force to back it up and replace it."
        }

        New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
        Copy-Item -LiteralPath $Destination -Destination (Join-Path $BackupDir ([IO.Path]::GetFileName($Destination))) -Force
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    return "installed: $Destination"
}

$gameRoot = Resolve-ExistingPath $GameDir
$dgInput = Resolve-ExistingPath $DgVoodooPath

$dgRoot = $dgInput
if ((Get-Item -LiteralPath $dgInput).PSIsContainer -eq $false) {
    if ([IO.Path]::GetExtension($dgInput) -ne ".zip") {
        throw "DgVoodooPath must be an extracted dgVoodoo folder or a .zip file."
    }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dgRoot = Join-Path $env:TEMP "dlss5-feed-dgvoodoo-$stamp"
    New-Item -ItemType Directory -Force -Path $dgRoot | Out-Null
    Expand-Archive -LiteralPath $dgInput -DestinationPath $dgRoot -Force
}

$d3d9 = Find-DgVoodooFile -Root $dgRoot -Name "D3D9.dll" -RequiredArch $Arch
$cpl = $null
try { $cpl = Find-DgVoodooFile -Root $dgRoot -Name "dgVoodooCpl.exe" } catch { $cpl = $null }

$backupDir = Join-Path $gameRoot ("dlss5-feed-backup\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$installed = @()
$installed += Copy-WithBackup -Source $d3d9 -Destination (Join-Path $gameRoot "D3D9.dll") -BackupDir $backupDir -Force:$Force

if ($cpl) {
    $installed += Copy-WithBackup -Source $cpl -Destination (Join-Path $gameRoot "dgVoodooCpl.exe") -BackupDir $backupDir -Force:$Force
}

$confPath = Join-Path $gameRoot "dgVoodoo.conf"
if ((Test-Path -LiteralPath $confPath) -and -not $Force) {
    throw "'$confPath' already exists. Re-run with -Force to back it up and replace it."
}
if (Test-Path -LiteralPath $confPath) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    Copy-Item -LiteralPath $confPath -Destination (Join-Path $backupDir "dgVoodoo.conf") -Force
}

$watermark = if ($NoWatermark) { "false" } else { "true" }
$conf = @"
[General]
OutputAPI = $OutputApi
FullScreenMode = false
EnumerateRefreshRates = false

[DirectX]
DisableAndPassThru = false
VideoCard = internal3D
VRAM = $VRAM
dgVoodooWatermark = $watermark
"@

Set-Content -LiteralPath $confPath -Value $conf -Encoding ASCII
$installed += "configured: $confPath"

[pscustomobject]@{
    GameDir = $gameRoot
    Arch = $Arch
    OutputApi = $OutputApi
    VRAM = $VRAM
    Watermark = -not $NoWatermark
    Installed = $installed
    BackupDir = if (Test-Path -LiteralPath $backupDir) { $backupDir } else { $null }
    NextStep = "Install ReShade as dxgi.dll after dgVoodoo is active; do not install ReShade as d3d9.dll for this DX9 path."
}
