@echo off
setlocal
cd /d "%~dp0\.."
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not defined SPOUT_SOURCE_DIR set "SPOUT_SOURCE_DIR=%CD%\build\spout-source"
if not exist "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.h" set "SPOUT_SOURCE_DIR=%CD%\..\_repos\Spout2"
if not exist "%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX\SpoutDX.h" set "SPOUT_SOURCE_DIR=%CD%\..\external\Spout2"
if not exist local-build\screen\SpoutDX.obj exit /b 1
cl /nologo /EHsc /std:c++20 /O2 /DNDEBUG /DSPOUT_BUILD_STATIC /MD /W4 /I"%SPOUT_SOURCE_DIR%\SPOUTSDK\SpoutDirectX\SpoutDX" /Fo:local-build\screen\ tests\screen-media-gpu-tests.cpp /Fe:local-build\screen\screen-media-gpu-tests.exe ^
 local-build\screen\SpoutDX.obj local-build\screen\SpoutCopy.obj local-build\screen\SpoutDirectX.obj local-build\screen\SpoutFrameCount.obj local-build\screen\SpoutSenderNames.obj local-build\screen\SpoutSharedMemory.obj local-build\screen\SpoutUtils.obj ^
 /link /MANIFEST:EMBED /DEBUG:NONE /INCREMENTAL:NO d3d11.lib dxgi.lib ole32.lib winmm.lib shlwapi.lib shell32.lib user32.lib gdi32.lib advapi32.lib version.lib
exit /b %errorlevel%
