@echo off
setlocal
cd /d "%~dp0\..\.."
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not defined PHOENIX_SOURCE_DIR set "PHOENIX_SOURCE_DIR=%CD%\..\dlss5-phoenix-addon"
if not exist "%PHOENIX_SOURCE_DIR%\third_party\nlohmann\json.hpp" exit /b 1
if not defined SPOUT_SOURCE_DIR set "SPOUT_SOURCE_DIR=%CD%\build\spout-source"
if not exist "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.h" set "SPOUT_SOURCE_DIR=%CD%\..\_repos\Spout2"
if not exist "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.h" set "SPOUT_SOURCE_DIR=%CD%\..\external\Spout2"
if not exist "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.h" exit /b 1
if not exist local-build\screen mkdir local-build\screen
if not exist runtime\screen mkdir runtime\screen
cl /nologo /std:c++20 /O2 /DNDEBUG /DSPOUT_BUILD_STATIC /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" /I"%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX" /Fo:local-build\screen\ ^
 source\screen\screen-worker.cpp /Fe:runtime\screen\dlss5-screen.exe ^
 "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.cpp" ^
 "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutCopy.cpp" "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutDirectX.cpp" ^
 "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutFrameCount.cpp" "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutSenderNames.cpp" ^
 "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutSharedMemory.cpp" "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutGL\SpoutUtils.cpp" ^
 /link /MANIFEST:EMBED /DEBUG:NONE /INCREMENTAL:NO d3d11.lib dxgi.lib d3dcompiler.lib user32.lib dwmapi.lib ole32.lib windowscodecs.lib windowsapp.lib ^
 winmm.lib shlwapi.lib shell32.lib gdi32.lib advapi32.lib version.lib mfplat.lib mf.lib mfreadwrite.lib mfuuid.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /EHsc /W4 /MD /Fo:local-build\screen\ tests\screen-contract-tests.cpp /Fe:local-build\screen\screen-contract-tests.exe
if errorlevel 1 exit /b 1
local-build\screen\screen-contract-tests.exe
exit /b %errorlevel%
