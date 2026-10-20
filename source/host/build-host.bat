@echo off
setlocal
cd /d "%~dp0\..\.."
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not defined PHOENIX_SOURCE_DIR set "PHOENIX_SOURCE_DIR=%CD%\..\dlss5-phoenix-addon"
if not exist "%PHOENIX_SOURCE_DIR%\third_party\nlohmann\json.hpp" (
  echo Set PHOENIX_SOURCE_DIR to the Phoenix checkout for its JSON header.
  exit /b 1
)
if not exist local-build\phoenix-host mkdir local-build\phoenix-host
if not defined DLSS5_HOST_OUTPUT_DIR set "DLSS5_HOST_OUTPUT_DIR=%CD%\runtime\host64"
if not exist "%DLSS5_HOST_OUTPUT_DIR%" mkdir "%DLSS5_HOST_OUTPUT_DIR%"
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" ^
   /Fo:local-build\phoenix-host\ /Fd:local-build\phoenix-host\host.pdb source\host\dlss5-feed-host64.cpp ^
   /Fe:"%DLSS5_HOST_OUTPUT_DIR%\dlss5-feed-host64.exe" ^
   /link /DEBUG:NONE /INCREMENTAL:NO d3d12.lib d3dcompiler.lib version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib windowscodecs.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /LD /Fo:local-build\phoenix-host\ ^
   source\host\phoenix-vulkan-feed.cpp /Fe:"%DLSS5_HOST_OUTPUT_DIR%\phoenix-vulkan-feed.addon64" ^
   /link /DEBUG:NONE /INCREMENTAL:NO /IMPLIB:local-build\phoenix-host\phoenix-vulkan-feed.lib user32.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" ^
   /Fo:local-build\phoenix-host\ tests\phoenix-host-config-tests.cpp /Fe:local-build\phoenix-host\phoenix-host-config-tests.exe /link d3dcompiler.lib
if errorlevel 1 exit /b 1
local-build\phoenix-host\phoenix-host-config-tests.exe
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" ^
   /Fo:local-build\phoenix-host\ tests\phoenix-host-feeder-tests.cpp /Fe:local-build\phoenix-host\phoenix-host-feeder-tests.exe ^
   /link /DEBUG:NONE /INCREMENTAL:NO d3d12.lib d3dcompiler.lib version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib windowscodecs.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /LD /Fo:local-build\phoenix-host\ ^
   tests\phoenix-host-feeder-fixture.cpp /Fe:local-build\phoenix-host\phoenix-feeder-good.dll ^
   /link /IMPLIB:local-build\phoenix-host\phoenix-feeder-good.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /LD /DPHOENIX_TEST_ABI=2 /Fo:local-build\phoenix-host\ ^
   tests\phoenix-host-feeder-fixture.cpp /Fe:local-build\phoenix-host\phoenix-feeder-wrong-version.dll ^
   /link /IMPLIB:local-build\phoenix-host\phoenix-feeder-wrong-version.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /LD /DPHOENIX_TEST_NO_EVALUATE /Fo:local-build\phoenix-host\ ^
   tests\phoenix-host-feeder-fixture.cpp /Fe:local-build\phoenix-host\phoenix-feeder-no-evaluate.dll ^
   /link /IMPLIB:local-build\phoenix-host\phoenix-feeder-no-evaluate.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /LD /DPHOENIX_TEST_NO_VERSION /Fo:local-build\phoenix-host\ ^
   tests\phoenix-host-feeder-fixture.cpp /Fe:local-build\phoenix-host\phoenix-feeder-no-version.dll ^
   /link /IMPLIB:local-build\phoenix-host\phoenix-feeder-no-version.lib
if errorlevel 1 exit /b 1
local-build\phoenix-host\phoenix-host-feeder-tests.exe local-build\phoenix-host\phoenix-feeder-good.dll ^
   local-build\phoenix-host\phoenix-feeder-wrong-version.dll local-build\phoenix-host\phoenix-feeder-no-evaluate.dll ^
   local-build\phoenix-host\phoenix-feeder-no-version.dll
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /O2 /DNDEBUG /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" ^
   /Fo:local-build\phoenix-host\ tests\phoenix-host-transport-probe.cpp /Fe:local-build\phoenix-host\phoenix-host-transport-probe.exe ^
   /link /DEBUG:NONE /INCREMENTAL:NO d3d12.lib ole32.lib windowscodecs.lib
if errorlevel 1 exit /b 1
local-build\phoenix-host\phoenix-host-transport-probe.exe --codec-selftest
if errorlevel 1 exit /b 1
local-build\phoenix-host\phoenix-host-transport-probe.exe --stereo-selftest
if errorlevel 1 exit /b 1
echo Phoenix host and Vulkan bridge compiled; CPU contracts passed. No GPU or inference test was run.
exit /b 0
