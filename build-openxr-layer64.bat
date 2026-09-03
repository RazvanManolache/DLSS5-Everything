@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist build\openxr-layer64 mkdir build\openxr-layer64
if not exist runtime\openxr mkdir runtime\openxr

set OPENXR_SDK_DIR=%OPENXR_SDK_DIR%
if not exist "%OPENXR_SDK_DIR%\include\openxr\openxr.h" (
  echo OpenXR SDK headers not found. Set OPENXR_SDK_DIR to a Khronos OpenXR-SDK checkout.
  exit /b 1
)

cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /Fobuild\openxr-layer64\ /Fdbuild\openxr-layer64\ ^
   /I"%OPENXR_SDK_DIR%\include" ^
   source\openxr-layer\openxr_layer.cpp ^
   /link /OUT:runtime\openxr\XR_APILAYER_DLSS5_everything.dll kernel32.lib user32.lib d3d11.lib

if errorlevel 1 exit /b 1
echo OpenXR layer built.
