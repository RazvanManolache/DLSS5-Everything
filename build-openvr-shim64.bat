@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist build\openvr-shim64 mkdir build\openvr-shim64
if not exist runtime\openvr mkdir runtime\openvr

cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /Fobuild\openvr-shim64\ /Fdbuild\openvr-shim64\ ^
   source\openvr-shim\openvr_api_proxy.cpp ^
   /link /OUT:runtime\openvr\dlss5-openvr-shim64.dll kernel32.lib

if errorlevel 1 exit /b 1
echo openvr shim built.
