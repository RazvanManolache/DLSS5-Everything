param(
    [Parameter(Mandatory=$true)][string]$PhoenixSdk,
    [string]$PayloadRoot = '',
    [string]$Model = '',
    [string]$NvidiaSdkArchive = '',
    [string]$CoreLibrary = ''
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$source = Join-Path $project 'external/dosbox-enhanced'
if (-not $PayloadRoot) { $PayloadRoot = Join-Path $project 'app/Dlss5CompatApp/Payload' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'build.ps1'))) {
    throw 'Initialize the source first: git submodule update --init --recursive external/dosbox-enhanced'
}
# DOSBox Enhanced owns its engine, native graphics bridge and dependency pins.
# Do not copy code from the obsolete Staging/ReShade feeder into this package.
& (Join-Path $source 'build.ps1') -Graphics -PhoenixSdk $PhoenixSdk -CoreLibrary $CoreLibrary
if ($LASTEXITCODE -ne 0) { throw 'DOSBox Enhanced source build failed.' }
if ($Model) {
    & (Join-Path $source 'setup-graphics.ps1') -Model $Model -NvidiaSdkArchive $NvidiaSdkArchive
}
$destination = Join-Path ([IO.Path]::GetFullPath($PayloadRoot)) 'dosbox-enhanced'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
function Copy-Atomic([string]$InputFile, [string]$OutputFile) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputFile)) -Force | Out-Null
    $stagedFile = $OutputFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::Copy($InputFile, $stagedFile, $false)
        # Replace the directory entry rather than writing through existing session hard links.
        [IO.File]::Move($stagedFile, $OutputFile, $true)
    } finally {
        if (Test-Path -LiteralPath $stagedFile) { Remove-Item -LiteralPath $stagedFile }
    }
}
foreach ($directory in @('app','runtime','licenses')) {
    $inputDirectory = Join-Path $source $directory
    if (Test-Path -LiteralPath $inputDirectory) {
        New-Item -ItemType Directory -Path (Join-Path $destination $directory) -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $inputDirectory -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
            Copy-Atomic $file.FullName (Join-Path $destination $relative)
        }
    }
}
foreach ($file in @('LICENSE','THIRD_PARTY_NOTICES.md','BUILDING.md','setup-graphics.ps1','runtime.lock.json')) {
    Copy-Atomic (Join-Path $source $file) (Join-Path $destination $file)
}
foreach ($file in @('DOSBOX_LICENSE','GRAPHICS.md','USER_GUIDE.md','MCP.md')) {
    Copy-Atomic (Join-Path $source ('docs/' + $file)) (Join-Path $destination ('docs/' + $file))
}
$revision = (git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read DOSBox Enhanced source revision.' }
$sourceChanges = git -C $source status --porcelain --ignore-submodules=all
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect DOSBox Enhanced source changes.' }
[ordered]@{ schema='dosbox-enhanced-source-build/1'; revision=$revision;
    modified=[bool]$sourceChanges; createdUtc=[DateTimeOffset]::UtcNow;
    appSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'app/DosboxEnhanced.exe')).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'source-build.json') -Encoding utf8
Write-Host ('DOSBox Enhanced staged: ' + $destination)
if (-not $Model) { Write-Host 'No model selected. Run its setup-graphics.ps1 with your separately supplied model before enabling DOS neural rendering.' }
