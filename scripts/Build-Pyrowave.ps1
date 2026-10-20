[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Prepare-Pyrowave.ps1')
$build = Join-Path $root 'build/pyrowave-win64'
# The workspace may have moved since the previous build. Regenerate only CMake's
# own cache instead of retaining absolute source paths from the old location.
& cmake --fresh -S (Join-Path $root 'source/codec') -B $build -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'Pyrowave CMake configure failed.' }
& cmake --build $build --config $Configuration --target dlss5-pyrowave --parallel 4
if ($LASTEXITCODE) { throw 'Pyrowave compile failed.' }
$runtime = Join-Path $root 'runtime/codec'
[void][IO.Directory]::CreateDirectory($runtime)
foreach ($name in @('dlss5-pyrowave.exe','phoenix-pyrowave.dll')) {
    Copy-Item -LiteralPath (Join-Path $build "$Configuration/$name") -Destination (Join-Path $runtime $name) -Force
}
Write-Host 'Pyrowave Windows runtime built and staged. It was not executed.'
