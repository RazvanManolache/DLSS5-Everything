@echo off
setlocal
if "%~1"=="" (
  echo Usage: run-openxr-layer-test.cmd "C:\Path\Game.exe" [arguments...]
  exit /b 2
)
set "XR_API_LAYER_PATH=%~dp0..\runtime\openxr"
set "XR_ENABLE_API_LAYERS=XR_APILAYER_DLSS5_everything"
set "XR_LOADER_DEBUG=all"
set "DLSS5_OPENXR_ENABLED=1"
set "DLSS5_OPENXR_WARMUP_RELEASES=0"
set "DLSS5_OPENXR_PROCESS_EVERY=1"
set "DLSS5_OPENXR_MAX_BLOCK_MS=1500"
set "DLSS5_OPENXR_PROCESS_TIMEOUT_MS=5000"
set "GAME_EXE=%~1"
set "GAME_DIR=%~dp1"
shift
start "" /D "%GAME_DIR%" "%GAME_EXE%" %*
