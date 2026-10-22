#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$script = Join-Path $project 'scripts/Copy-DistributionNotices.ps1'
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $temp ('distribution-notices-smoke-' + [guid]::NewGuid().ToString('N'))
$checks = 0

function Require([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Write-Fixture([string] $Path, [string] $Text) {
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Must-Fail([scriptblock] $Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Require $failed 'Expected incomplete or mismatched notice inputs to fail closed.'
}

try {
    [void] [IO.Directory]::CreateDirectory($root)
    $repo = Join-Path $root 'fixture-project'
    $package = Join-Path $root 'publish'
    $nuget = Join-Path $root 'nuget-cache'
    $assets = Join-Path $root 'assets.json'
    $header = Join-Path $root 'json.hpp'
    Write-Fixture (Join-Path $repo 'source/external/imgui/LICENSE.txt') ([IO.File]::ReadAllText((Join-Path $project 'source/external/imgui/LICENSE.txt')))
    Write-Fixture (Join-Path $repo 'source/external/spout/LICENSE') ([IO.File]::ReadAllText((Join-Path $project 'source/external/spout/LICENSE')))
    $compressionLicense = Join-Path $repo 'source/external/sharpcompress/LICENSE.txt'
    Write-Fixture $compressionLicense ([IO.File]::ReadAllText((Join-Path $project 'source/external/sharpcompress/LICENSE.txt')))
    $sdk = Join-Path $repo 'source/external/reshade/include'
    [void] [IO.Directory]::CreateDirectory($sdk)
    Copy-Item -LiteralPath (Join-Path $project 'source/external/reshade/include/reshade.hpp') -Destination $sdk
    Write-Fixture $header @'
// SPDX-FileCopyrightText: 2013 - 2025 Niels Lohmann <https://nlohmann.me>
// SPDX-FileCopyrightText: 2016 - 2021 Evan Nemerson <evan@nemerson.com>
// SPDX-FileCopyrightText: 2018 The Abseil Authors
// SPDX-FileCopyrightText: 2008 - 2009 Bjoern Hoehrmann <bjoern@hoehrmann.de>
// SPDX-FileCopyrightText: 2009 Florian Loitsch <https://florian.loitsch.com/>
// SPDX-License-Identifier: MIT
// Embedded notice context must remain byte-for-byte intact.
'@
    $folders = [ordered]@{}
    $folders[(Join-Path $root 'empty-first-cache')] = @{}
    $folders[$nuget] = @{}
    Write-Fixture $assets (@{ packageFolders = $folders } | ConvertTo-Json -Depth 5)
    $frameworks = @(
        @{ name = 'Microsoft.NETCore.App'; version = '9.0.20' }
        @{ name = 'Microsoft.WindowsDesktop.App'; version = '9.0.20' }
    )
    Write-Fixture (Join-Path $package 'Dlss5CompatApp.runtimeconfig.json') (@{ runtimeOptions = @{ includedFrameworks = $frameworks } } | ConvertTo-Json -Depth 6)
    $libraries = [ordered]@{}
    foreach ($framework in $frameworks) {
        $id = "$($framework.name).Runtime.win-x64"
        $libraries["runtimepack.$id/9.0.20"] = @{ type = 'runtimepack' }
        Write-Fixture (Join-Path $nuget "$($id.ToLowerInvariant())/9.0.20/LICENSE.TXT") "$id EXACT VERSION 9.0.20 license fixture"
        Write-Fixture (Join-Path $nuget "$($id.ToLowerInvariant())/9.0.99/LICENSE.TXT") 'WRONG newer version must not be copied'
    }
    $coreNotices = Join-Path $nuget 'microsoft.netcore.app.runtime.win-x64/9.0.20/THIRD-PARTY-NOTICES.TXT'
    Write-Fixture $coreNotices 'Exact Core third-party notice fixture, including all component terms.'
    $depsPath = Join-Path $package 'Dlss5CompatApp.deps.json'
    $depsText = @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v9.0/win-x64' }; libraries = $libraries } | ConvertTo-Json -Depth 6
    Write-Fixture $depsPath $depsText
    $parameters = @{ PackageRoot = $package; JsonHeaderPath = $header; ProjectAssetsPath = $assets; ProjectRoot = $repo }
    & $script @parameters
    $licenses = Join-Path $package 'Licenses'
    $inventory = Get-Content -LiteralPath (Join-Path $licenses 'inventory.json') -Raw | ConvertFrom-Json
    Require ($inventory.Frameworks.Count -eq 2 -and @($inventory.Frameworks | Where-Object Version -ne '9.0.20').Count -eq 0) 'Resolved runtime versions were not retained exactly.'
    Require ((Get-FileHash -LiteralPath $header).Hash -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'nlohmann-json/json.hpp.txt')).Hash) 'The full JSON header must be retained unchanged.'
    Require ((Get-FileHash -LiteralPath $coreNotices).Hash -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'dotnet/Microsoft.NETCore.App/THIRD-PARTY-NOTICES.TXT')).Hash) 'Runtime third-party notices must be copied unchanged.'
    Require ((Get-FileHash -LiteralPath $compressionLicense).Hash -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'SharpCompress/LICENSE.txt')).Hash) 'SharpCompress license must be copied unchanged.'
    $sdkLicense = Get-Content -LiteralPath (Join-Path $licenses 'ReShade-SDK/LICENSE.txt') -Raw
    Require ($sdkLicense.Contains('Patrick Mours') -and $sdkLicense.Contains('Permission is hereby granted') -and $sdkLicense.Contains('THE SOFTWARE IS PROVIDED "AS IS"')) 'SDK MIT permission, attribution, or disclaimer is missing.'
    $jsonLicense = Get-Content -LiteralPath (Join-Path $licenses 'nlohmann-json/LICENSE.txt') -Raw
    foreach ($name in 'Niels Lohmann', 'Evan Nemerson', 'The Abseil Authors', 'Bjoern Hoehrmann', 'Florian Loitsch') {
        Require ($jsonLicense.Contains($name)) "JSON attribution was dropped: $name"
    }
    $files = @(Get-ChildItem -LiteralPath $licenses -Recurse -File)
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        Require (!$text.Contains($root) -and !$text.Contains('WRONG newer version')) 'Private build path or unselected runtime license leaked into the package.'
    }
    Require (@($files | Where-Object Extension -notin '.txt', '.json', '.md').Count -eq 0) 'Notice helper copied a binary or other payload.'
    foreach ($notice in $inventory.Notices) {
        Require ($notice.Sha256 -eq (Get-FileHash -LiteralPath (Join-Path $licenses $notice.Path)).Hash) 'Inventory hash does not describe the copied notice bytes.'
    }
    $before = (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash
    & $script @parameters
    Require ($before -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash) 'Repeated copying must be deterministic.'

    $missingPackage = Join-Path $root 'missing-notice-publish'
    [void] [IO.Directory]::CreateDirectory($missingPackage)
    Copy-Item -LiteralPath (Join-Path $package 'Dlss5CompatApp.runtimeconfig.json'), $depsPath -Destination $missingPackage
    $saved = [IO.File]::ReadAllText($coreNotices)
    [IO.File]::Delete($coreNotices)
    Must-Fail { & $script @parameters -PackageRoot $missingPackage }
    Require (!(Test-Path -LiteralPath (Join-Path $missingPackage 'Licenses'))) 'Input validation failure must not mutate package notices.'
    Write-Fixture $coreNotices $saved

    Write-Fixture $depsPath ($depsText.Replace('/9.0.20', '/9.0.99'))
    Must-Fail { & $script @parameters }
    Require ($before -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash) 'Mismatched runtime metadata changed the existing package.'
    Write-Fixture $depsPath $depsText
    $runtimePath = Join-Path $package 'Dlss5CompatApp.runtimeconfig.json'
    $runtimeText = [IO.File]::ReadAllText($runtimePath)
    Write-Fixture $runtimePath ($runtimeText.Replace('9.0.20', '9.0.21'))
    Write-Fixture $depsPath ($depsText.Replace('/9.0.20', '/9.0.21'))
    Must-Fail { & $script @parameters }
    Require ($before -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash) 'Missing exact runtime pack must not fall back to another installed version.'
    Write-Fixture $runtimePath $runtimeText
    Write-Fixture $depsPath $depsText
    $jsonText = [IO.File]::ReadAllText($header)
    Write-Fixture $header ($jsonText.Replace('SPDX-License-Identifier: MIT', 'SPDX-License-Identifier: GPL-3.0-only'))
    Must-Fail { & $script @parameters }
    Require ($before -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash) 'Unreviewed JSON license changed existing notices.'
    Write-Fixture $header $jsonText
    $software = Join-Path $package 'Runtime/software-reshade'
    Write-Fixture (Join-Path $software 'ReShadeSoftware64.dll') 'existence fixture, never executed'
    Must-Fail { & $script @parameters }
    Require ($before -eq (Get-FileHash -LiteralPath (Join-Path $licenses 'inventory.json')).Hash) 'A software runtime without its notices changed the package.'
    foreach ($name in @('LICENSE.md', 'DEPENDENCIES.txt', 'BUILD.json')) {
        Write-Fixture (Join-Path $software $name) "Retained software-hook notice fixture: $name"
    }
    & $script @parameters
    foreach ($name in @('LICENSE.md', 'DEPENDENCIES.txt', 'BUILD.json')) {
        Require ((Get-FileHash -LiteralPath (Join-Path $software $name)).Hash -eq
            (Get-FileHash -LiteralPath (Join-Path $licenses "ReShade-Software/$name")).Hash) 'Software-hook notice bytes were not retained.'
    }
    Write-Host "$checks distribution notice smoke checks passed; no network, native builds, GPU, or registry access."
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (!$resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^distribution-notices-smoke-[0-9a-f]{32}$') { throw 'Unsafe smoke fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
