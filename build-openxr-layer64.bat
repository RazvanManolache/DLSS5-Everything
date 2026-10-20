@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not exist build\openxr-layer64 mkdir build\openxr-layer64
if not defined OPENXR_LAYER_OUTPUT_DIR set "OPENXR_LAYER_OUTPUT_DIR=runtime\openxr"
if not exist "%OPENXR_LAYER_OUTPUT_DIR%" mkdir "%OPENXR_LAYER_OUTPUT_DIR%"

set OPENXR_SDK_DIR=%OPENXR_SDK_DIR%
if not exist "%OPENXR_SDK_DIR%\include\openxr\openxr.h" (
  echo OpenXR SDK headers not found. Set OPENXR_SDK_DIR to a Khronos OpenXR-SDK checkout.
  exit /b 1
)

cl /nologo /LD /EHsc /O2 /DNDEBUG /MD /W3 /std:c++20 /Fobuild\openxr-layer64\ /Fdbuild\openxr-layer64\ ^
   /I"%OPENXR_SDK_DIR%\include" ^
   source\openxr-layer\openxr_layer.cpp ^
   /link /DEBUG:NONE /INCREMENTAL:NO /OUT:"%OPENXR_LAYER_OUTPUT_DIR%\XR_APILAYER_DLSS5_everything.dll" /IMPLIB:build\openxr-layer64\openxr_layer.lib kernel32.lib user32.lib d3d11.lib

if errorlevel 1 exit /b 1
if /i "%~1"=="--compile-only" exit /b 0
cl /nologo /EHsc /O2 /DNDEBUG /MD /W3 /std:c++20 /Fobuild\openxr-layer64\ /Fdbuild\openxr-layer64\ ^
   /I"%OPENXR_SDK_DIR%\include" tests\openxr-profile-tests.cpp ^
   /link /OUT:build\openxr-layer64\openxr-profile-tests.exe kernel32.lib user32.lib d3d11.lib
if errorlevel 1 exit /b 1
build\openxr-layer64\openxr-profile-tests.exe
if errorlevel 1 exit /b 1
echo OpenXR layer built; native CPU profile/IPC checks passed. No host, GPU or inference executed.
exit /b 0
