#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'build/spout-source'
$revision = '6b68a5b4fdeca6130b851e5f9366467a844860e5'
if (!(Test-Path -LiteralPath (Join-Path $target '.git'))) {
    if (Test-Path -LiteralPath $target) { throw 'Spout build directory exists but is not a Git checkout.' }
    git clone --no-checkout https://github.com/leadedge/Spout2.git $target
    if ($LASTEXITCODE) { throw 'Spout clone failed.' }
}
if (git -C $target status --porcelain) { throw 'Refusing to change a dirty Spout checkout.' }
git -C $target checkout --detach $revision
if ($LASTEXITCODE) { throw 'Pinned Spout checkout failed.' }
$env:SPOUT_SOURCE_DIR = $target
Write-Host 'Pinned BSD-licensed Spout source is ready. No model or proprietary runtime downloaded.'
