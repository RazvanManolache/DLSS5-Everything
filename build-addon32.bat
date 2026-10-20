@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvarsamd64_x86.bat" >nul
if errorlevel 1 exit /b 1
if not exist build mkdir build
if /i "%~1"=="--contract-test" goto contract_test
if not exist runtime\x86-dx9-dx11 mkdir runtime\x86-dx9-dx11
cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /Isource\external\reshade\include /Isource\external\imgui /Fobuild\ /Fdbuild\ ^
   source\src\dlss5-feed32.cpp ^
   /link /OUT:runtime\x86-dx9-dx11\dlss5-feed.addon32 /IMPLIB:build\dlss5-feed32.lib d3d9.lib d3d11.lib opengl32.lib kernel32.lib user32.lib advapi32.lib
if errorlevel 1 exit /b 1
echo addon32 built.
exit /b 0

:contract_test
cl /nologo /EHsc /O2 /MD /W3 /std:c++20 /Gy /DFEED_PHOENIX_CONTRACT_TEST /Isource\external\reshade\include /Isource\external\imgui ^
   /Fobuild\phoenix-feeder-contract32.obj /Fdbuild\phoenix-feeder-contract32.pdb source\src\dlss5-feed32.cpp ^
   /link /OPT:REF /OUT:build\phoenix-feeder-contract32.exe /IMPLIB:build\phoenix-feeder-contract32.lib ^
   d3d9.lib d3d11.lib dxgi.lib d3dcompiler.lib opengl32.lib kernel32.lib user32.lib advapi32.lib
if errorlevel 1 exit /b 1
build\phoenix-feeder-contract32.exe
exit /b %errorlevel%
