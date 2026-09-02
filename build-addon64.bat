@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist build\x64 mkdir build\x64

cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /Isource\external\reshade\include /Isource\external\imgui /Fobuild\x64\ /Fdbuild\x64\ ^
   source\src\dlss5-feed32.cpp ^
   user32.lib d3d11.lib d3d9.lib dxgi.lib d3dcompiler.lib opengl32.lib ^
   /link /OUT:build\x64\dlss5-feed.addon64

if errorlevel 1 exit /b 1
if not exist runtime\x64-dx9-dx11 mkdir runtime\x64-dx9-dx11
copy /y build\x64\dlss5-feed.addon64 runtime\x64-dx9-dx11\dlss5-feed.addon64 >nul
