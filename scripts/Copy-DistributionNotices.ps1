#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageRoot,
    [string] $JsonHeaderPath,
    [string] $ProjectAssetsPath,
    [string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$PackageRoot = (Get-Item -LiteralPath $PackageRoot).FullName
if (!(Test-Path -LiteralPath $PackageRoot -PathType Container)) { throw 'PackageRoot must be an existing publish directory.' }
if (!$ProjectAssetsPath) { $ProjectAssetsPath = Join-Path $ProjectRoot 'app/Dlss5CompatApp/obj/project.assets.json' }
if (!$JsonHeaderPath -and $env:PHOENIX_SOURCE_DIR) { $JsonHeaderPath = Join-Path $env:PHOENIX_SOURCE_DIR 'third_party/nlohmann/json.hpp' }
if (!$JsonHeaderPath) {
    $candidates = @(
        (Join-Path (Split-Path -Parent $ProjectRoot) 'dlss5-phoenix-addon/third_party/nlohmann/json.hpp')
        (Join-Path $ProjectRoot 'build/phoenix-source/third_party/nlohmann/json.hpp')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    if (!$candidates) { throw 'Supply -JsonHeaderPath for the exact nlohmann header used to build the host.' }
    $JsonHeaderPath = @($candidates)[0]
}

function Read-RequiredText([string] $Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required notice input is missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($text)) { throw "Required notice input is empty: $Path" }
    return $text
}

function Assert-NoLinks([string] $Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Notice output must not follow a link: $current"
        }
    }
}

$plan = [Collections.Generic.List[object]]::new()
function Add-Notice([string] $RelativePath, [string] $SourceId, [string] $SourcePath, [string] $Text) {
    $bytes = if ($SourcePath) { [IO.File]::ReadAllBytes($SourcePath) } else { [Text.UTF8Encoding]::new($false).GetBytes($Text) }
    if (!$bytes.Length) { throw "Empty distribution notice: $RelativePath" }
    $plan.Add([pscustomobject]@{ Path = $RelativePath; Source = $SourceId; Bytes = $bytes })
}

$imguiPath = Join-Path $ProjectRoot 'source/external/imgui/LICENSE.txt'
$imgui = Read-RequiredText $imguiPath
$permissionIndex = $imgui.IndexOf('Permission is hereby granted, free of charge, to any person obtaining a copy', [StringComparison]::Ordinal)
if ($permissionIndex -lt 0 -or !$imgui.Contains('THE SOFTWARE IS PROVIDED "AS IS"')) { throw 'Vendored ImGui MIT permission/disclaimer text is missing.' }
# The verbatim MIT permission/disclaimer is shared by these MIT-licensed inputs, not reconstructed from a summary.
$mitPermission = $imgui.Substring($permissionIndex).TrimEnd()
Add-Notice 'DearImGui/LICENSE.txt' 'source/external/imgui/LICENSE.txt' $imguiPath ''
$spoutPath = Join-Path $ProjectRoot 'source/external/spout/LICENSE'
$spoutLicense = Read-RequiredText $spoutPath
if (!$spoutLicense.Contains('BSD 2-Clause License') -or !$spoutLicense.Contains('Lynn Jarvis')) { throw 'Spout BSD license is missing.' }
Add-Notice 'Spout/LICENSE.txt' 'https://github.com/leadedge/Spout2/tree/6b68a5b4fdeca6130b851e5f9366467a844860e5' $spoutPath ''
foreach ($notice in @(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'source/codec/licenses') -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
    Add-Notice "Pyrowave/$($notice.Name)" "source/codec/licenses/$($notice.Name)" $notice.FullName ''
}

$softwareRuntime = Join-Path $PackageRoot 'Runtime/software-reshade'
if ((Test-Path -LiteralPath (Join-Path $softwareRuntime 'ReShadeSoftware64.dll')) -or
    (Test-Path -LiteralPath (Join-Path $softwareRuntime 'ReShadeSoftware32.dll'))) {
    foreach ($name in @('LICENSE.md', 'DEPENDENCIES.txt', 'BUILD.json')) {
        $source = Join-Path $softwareRuntime $name
        [void] (Read-RequiredText $source)
        Add-Notice "ReShade-Software/$name" 'https://github.com/crosire/reshade/tree/18deaa52de0c425a78b329e9cb3c497281cd00ec (modified opt-in software hooks)' $source ''
    }
}

$sdkHeaders = @(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'source/external/reshade/include') -Filter '*.hpp' -File | Sort-Object Name)
if (!$sdkHeaders.Count) { throw 'ReShade SDK headers are missing.' }
$sdkNotices = [Collections.Generic.List[string]]::new()
$sdkCopyrights = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($header in $sdkHeaders) {
    $text = Read-RequiredText $header.FullName
    $block = [regex]::Match($text, '\A\s*/\*[\s\S]*?\*/').Value
    if ($block -notmatch 'SPDX-License-Identifier:\s*(?:BSD-3-Clause OR MIT|MIT)\s*\r?\n' -or $block -notmatch 'Copyright') {
        throw "ReShade header has an unrecognized license declaration: $($header.Name)"
    }
    $sdkNotices.Add("$($header.Name)`n$block")
    foreach ($match in [regex]::Matches($block, '(?m)^\s*\*?\s*(Copyright[^\r\n]*)')) {
        [void] $sdkCopyrights.Add($match.Groups[1].Value.Trim())
    }
}
$sdkLicense = "MIT License (MIT option selected for ReShade SDK headers)`n`n" +
    (($sdkCopyrights | Sort-Object) -join "`n") + "`n`n$mitPermission`n"
Add-Notice 'ReShade-SDK/LICENSE.txt' 'https://github.com/crosire/reshade/tree/main/include (header MIT option)' '' $sdkLicense
Add-Notice 'ReShade-SDK/HEADER-NOTICES.txt' 'source/external/reshade/include/*.hpp' '' ($sdkNotices -join "`n`n")

$json = Read-RequiredText $JsonHeaderPath
$jsonLicenses = @([regex]::Matches($json, '(?m)^\s*//\s*SPDX-License-Identifier:\s*([^\r\n]+)') | ForEach-Object { $_.Groups[1].Value.Trim() } | Sort-Object -Unique)
if (!$jsonLicenses.Count -or @($jsonLicenses | Where-Object { $_ -ne 'MIT' }).Count -or $json -notmatch 'Niels Lohmann') {
    throw 'The selected nlohmann header is missing its MIT notices or declares additional license terms requiring review.'
}
$jsonCopyrights = @([regex]::Matches($json, '(?m)^\s*//\s*SPDX-FileCopyrightText:\s*([^\r\n]+)') |
    ForEach-Object { 'Copyright ' + $_.Groups[1].Value.Trim() } | Sort-Object -Unique)
if (!$jsonCopyrights.Count) { throw 'The selected nlohmann header has no retained copyright declarations.' }
$jsonLicense = "MIT License`n`n" + ($jsonCopyrights -join "`n") + "`n`n$mitPermission`n"
Add-Notice 'nlohmann-json/LICENSE.txt' 'https://github.com/nlohmann/json (MIT; copyrights from build header)' '' $jsonLicense
# Keep the complete build header unchanged so embedded component notices and their context cannot be lost by extraction.
Add-Notice 'nlohmann-json/json.hpp.txt' 'nlohmann/json.hpp (exact host build input)' $JsonHeaderPath ''

$assets = (Read-RequiredText $ProjectAssetsPath) | ConvertFrom-Json -AsHashtable
$compressionLicense = Join-Path $ProjectRoot 'source/external/sharpcompress/LICENSE.txt'
[void] (Read-RequiredText $compressionLicense)
Add-Notice 'SharpCompress/LICENSE.txt' 'https://github.com/adamhathcock/sharpcompress/blob/0.50.7/LICENSE.txt' $compressionLicense ''
$runtime = (Read-RequiredText (Join-Path $PackageRoot 'Dlss5CompatApp.runtimeconfig.json')) | ConvertFrom-Json -AsHashtable
$deps = (Read-RequiredText (Join-Path $PackageRoot 'Dlss5CompatApp.deps.json')) | ConvertFrom-Json -AsHashtable
$frameworks = @($runtime.runtimeOptions.includedFrameworks)
if (!$frameworks.Count -or !$assets.packageFolders.Count) { throw 'Self-contained includedFrameworks and restored NuGet packageFolders are required.' }
$rid = ($deps.runtimeTarget.name -split '/')[-1]
if ($rid -notmatch '^win-(x64|x86|arm64)$') { throw 'The published app must identify a supported Windows runtime identifier.' }
$runtimeInventory = [Collections.Generic.List[object]]::new()
foreach ($framework in $frameworks) {
    if ($framework.name -notin 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App' -or
        $framework.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Unsupported or invalid published runtime framework.' }
    $id = "$($framework.name).Runtime.$rid"
    $identity = "$id/$($framework.version)"
    $library = "runtimepack.$identity"
    if (!$deps.libraries.Contains($library) -or $deps.libraries[$library].type -ne 'runtimepack') {
        throw "Published runtimeconfig/deps disagree about runtime pack: $identity"
    }
    $pack = $null
    foreach ($folder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $folder $identity.ToLowerInvariant()
        if (Test-Path -LiteralPath $candidate -PathType Container) { $pack = $candidate; break }
    }
    if (!$pack) { throw "Exact resolved runtime pack is not available in restored package folders: $identity" }
    $files = @(Get-ChildItem -LiteralPath $pack -File)
    $licenses = @($files | Where-Object { $_.Name -match '^LICENSE(?:\.(txt|md))?$' })
    $notices = @($files | Where-Object { $_.Name -match '^third[-_]?party[-_]?notices(?:\.(txt|md))?$' })
    if ($licenses.Count -ne 1 -or ($framework.name -eq 'Microsoft.NETCore.App' -and !$notices.Count)) {
        throw "Runtime pack license or required Core third-party notices are missing: $identity"
    }
    Add-Notice "dotnet/$($framework.name)/LICENSE.txt" "nuget:$identity/$($licenses[0].Name)" $licenses[0].FullName ''
    foreach ($notice in $notices) {
        Add-Notice "dotnet/$($framework.name)/$($notice.Name)" "nuget:$identity/$($notice.Name)" $notice.FullName ''
    }
    $runtimeInventory.Add([ordered]@{ Framework = $framework.name; Version = $framework.version; Package = $id })
}
if (@($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -ne 1 -or
    @($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }).Count -ne 1) {
    throw 'Both Core and Windows Desktop runtime licenses are required for this self-contained WinForms app.'
}

$index = [Collections.Generic.List[string]]::new()
$index.Add('# Distribution Licenses')
$index.Add('')
$index.Add('Retained texts for the first-party feeders/host and this exact self-contained .NET publish. No model or vendor runtime is included by this notice helper.')
$index.Add('')
$inventory = [Collections.Generic.List[object]]::new()
foreach ($entry in $plan) {
    $index.Add("- [$($entry.Path)]($($entry.Path))")
    $inventory.Add([ordered]@{ Path = $entry.Path; Source = $entry.Source; Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.Bytes)) })
}
$manifest = [ordered]@{ Schema = 1; RuntimeIdentifier = $rid; Frameworks = @($runtimeInventory); Notices = @($inventory) }
Add-Notice 'inventory.json' 'distribution notice inventory (no local source paths)' '' ($manifest | ConvertTo-Json -Depth 8)
Add-Notice 'README.md' 'distribution notice index' '' (($index -join "`n") + "`n")

# All inputs are resolved and validated before the first package mutation. Only these explicit text files are copied.
foreach ($entry in $plan) {
    $destination = Join-Path $PackageRoot "Licenses/$($entry.Path)"
    Assert-NoLinks $destination
}
foreach ($entry in $plan) {
    $destination = Join-Path $PackageRoot "Licenses/$($entry.Path)"
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::WriteAllBytes($destination, $entry.Bytes)
}
Write-Host "Copied $($plan.Count) distribution notice files for $rid; exact resolved framework versions retained in Licenses/inventory.json."
