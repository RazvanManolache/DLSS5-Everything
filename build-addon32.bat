@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvarsamd64_x86.bat" >nul
if not exist build mkdir build
cl /nologo /LD /EHsc /O2 /MD /W3 /std:c++20 /Isource\external\reshade\include /Isource\external\imgui /Fobuild\ /Fdbuild\ ^
   source\src\dlss5-feed32.cpp ^
   /link /OUT:build\dlss5-feed.addon32 d3d11.lib kernel32.lib user32.lib advapi32.lib
if errorlevel 1 exit /b 1
echo addon32 built.
