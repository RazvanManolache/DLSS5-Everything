param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$InputImage,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 32)][int]$Frames = 4,
    [ValidateSet('Float', 'Rgba8', 'Bgra8')][string]$ImageFormat = 'Float',
    [ValidateSet('Image', 'Cpu', 'Shared', 'SharedBgra', 'SharedStereo', 'SharedBgraStereo')][string]$Transport = 'Image',
    [ValidateRange(1, 10)][int]$Iterations = 1,
    [switch]$Bypass,
    [switch]$InputOnly,
    [string]$HostBinary = (Join-Path $PSScriptRoot '../runtime/host64/dlss5-feed-host64.exe')
)
$ErrorActionPreference = 'Stop'
if ($InputOnly -and ($Transport -ne 'Image' -or $Bypass)) {
    throw '-InputOnly requires -Transport Image and cannot be combined with -Bypass.'
}
$payload = (Resolve-Path -LiteralPath $PayloadRoot).Path
$inputFile = (Resolve-Path -LiteralPath $InputImage).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new empty output directory; existing files will not be changed.' }
if ($output -eq [IO.Path]::GetPathRoot($output)) { throw 'A drive root is not a test directory.' }
$hostBinary = (Resolve-Path -LiteralPath $HostBinary).Path
$transportProbe = Join-Path $PSScriptRoot '../local-build/phoenix-host/phoenix-host-transport-probe.exe'
$stereo = $Transport.EndsWith('Stereo', [StringComparison]::OrdinalIgnoreCase)
if ($stereo -and ($Frames -lt 4 -or $Iterations -ne 1)) {
    throw 'Stereo continuity probes require 4..8 frames and one iteration.'
}
if ($Transport -ne 'Image' -and (!(Test-Path -LiteralPath $transportProbe) -or $Bypass -or $Frames -gt 8)) {
    throw 'Transport probes require the built native test client, 1..8 frames, and no -Bypass (an exact bypass frame is tested internally).'
}
$required = @('phoenix.addon64', 'ReShade64.dll', 'nvngx_dlssnr.dll')
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Payload missing: $name" }
}
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $hostBinary -Destination (Join-Path $output 'dlss5-feed-host64.exe')
Copy-Item -LiteralPath (Join-Path $payload 'ReShade64.dll') -Destination (Join-Path $output 'dxgi.dll')
Copy-Item -LiteralPath (Join-Path $payload 'phoenix.addon64') -Destination (Join-Path $output 'Phoenix.addon64')
Copy-Item -LiteralPath (Join-Path $payload 'nvngx_dlssnr.dll') -Destination (Join-Path $output 'PhoenixModel.dll')
$model = Join-Path $output 'PhoenixModel.dll'
$config = [ordered]@{
    schema = 'phoenix-live/1'; armed = !$Bypass; input_provider = 'feeder_d3d12'; direct_transfer = 'srgb'
    sandbox = $output; in_place_game_directory = $output
    in_place_approval = 'I approve driving this installed game in place'
    weight_source = @{ kind = 'nvidia_dll'; path = $model; sha256 = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant() }
    frame_limit = 128; color_domain = 'game_nr_sdr_proxy'; require_gpu_ready_file = $false
}
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'Phoenix.json') -Encoding utf8NoBOM
$ini = @(
    '[GENERAL]', 'TutorialProgress=4', 'NoReloadOnInit=1',
    '[OVERLAY]', 'TutorialProgress=4', 'ShowOverlay=0',
    '[INPUT]', 'KeyOverlay=0,0,0,0', 'KeyScreenshot=0,0,0,0',
    '[Phoenix]', ('Backend=' + $(if ($Bypass -or $InputOnly) { '0' } else { '3' })),
    ('NeuralUplift=' + $(if ($Bypass) { '0' } else { '1' })),
    'ExecutionDevice=native', 'ModelSource=configured', 'Intensity=1',
    'RefinementPasses=1', 'ResolutionMode=0', 'Comparison=0',
    'SourceA=0', 'SourceB=2', 'Divider=0.5'
)
$ini | Set-Content -LiteralPath (Join-Path $output 'ReShade.ini') -Encoding utf8NoBOM
$rendered = Join-Path $output 'output.png'
$imageArgument = switch ($ImageFormat) { 'Rgba8' { '--image-rgba8' }; 'Bgra8' { '--image-bgra8' }; default { '--image' } }
$arguments = @($imageArgument, ('"' + $inputFile + '"'), ('"' + $rendered + '"'), $Frames)
$program = Join-Path $output 'dlss5-feed-host64.exe'
if ($Transport -ne 'Image') {
    $ImageFormat = 'Rgba8'
    $route = switch ($Transport) {
        'SharedBgra' { 'shared-bgra' }
        'SharedStereo' { 'shared-stereo' }
        'SharedBgraStereo' { 'shared-bgra-stereo' }
        default { $Transport.ToLowerInvariant() }
    }
    $arguments = @(('"' + $program + '"'), ('"' + $inputFile + '"'), ('"' + $rendered + '"'), $route, $Frames, $Iterations)
    $program = $transportProbe
}
$process = Start-Process -FilePath $program -WorkingDirectory $output -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'probe.stdout.log') -RedirectStandardError (Join-Path $output 'probe.stderr.log')
if (!$process.WaitForExit(180000)) {
    $process.Kill()
    $process.WaitForExit()
    throw 'Isolated host probe timed out; only the probe process was stopped.'
}
[pscustomobject]@{
    ExitCode = $process.ExitCode
    OutputDirectory = $output
    OutputImage = $(if (Test-Path -LiteralPath $rendered) { $rendered } else { $null })
    Bypass = $Bypass.IsPresent
    InputOnly = $InputOnly.IsPresent
    ImageFormat = $ImageFormat
    Transport = $Transport
    EyeCount = $(if ($stereo) { 2 } else { 1 })
    InputProvider = 'feeder_d3d12'
    SuperResolution = $false
    InferenceVerified = $false
    Note = 'Inspect Phoenix runtime evidence and compare output pixels; host exit/evaluate counts alone do not verify inference.'
}
if ($process.ExitCode -ne 0) { throw "Host exited with code $($process.ExitCode). Inspect the isolated logs." }
