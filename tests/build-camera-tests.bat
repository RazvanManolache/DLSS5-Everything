@echo off
setlocal
cd /d "%~dp0\.."
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if not defined PHOENIX_SOURCE_DIR set "PHOENIX_SOURCE_DIR=%CD%\..\dlss5-phoenix-addon"
if not exist local-build\camera-current mkdir local-build\camera-current
cl /nologo /std:c++20 /O2 /EHsc /W4 /MD /I"%PHOENIX_SOURCE_DIR%\third_party" /Fo:local-build\camera-current\ tests\camera-tests.cpp /Fe:local-build\camera-current\camera-tests.exe /link /MANIFEST:EMBED d3d11.lib dxgi.lib ole32.lib windowsapp.lib mfplat.lib mf.lib mfreadwrite.lib mfuuid.lib
if errorlevel 1 exit /b 1
local-build\camera-current\camera-tests.exe %*
exit /b %errorlevel%
