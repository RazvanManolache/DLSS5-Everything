@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if not exist source\external\ngx\nvsdk_ngx.h (
  echo NGX SDK headers not found. Put them in source\external\ngx.
  exit /b 1
)
if not exist source\external\ngx\libs\nvsdk_ngx_d.lib (
  echo NGX SDK import library not found: source\external\ngx\libs\nvsdk_ngx_d.lib
  exit /b 1
)
cl /nologo /O2 /EHsc /W3 /MD /Isource\external\ngx source\host\dlss5-feed-host64.cpp ^
   /Fe:runtime\host64\dlss5-feed-host64.exe ^
   /link source\external\ngx\libs\nvsdk_ngx_d.lib version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib windowscodecs.lib
if errorlevel 1 exit /b 1
echo host built.
