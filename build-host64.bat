@echo off
setlocal
cd /d "%~dp0"
call "%ProgramFiles(x86)%\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
set "NGX_INCLUDE=source\external\ngx"
set "NGX_LIB=source\external\ngx\libs\nvsdk_ngx_d.lib"
if not exist "%NGX_INCLUDE%\nvsdk_ngx.h" (
  if defined DLSS_SDK_DIR (
    set "NGX_INCLUDE=%DLSS_SDK_DIR%\include"
    set "NGX_LIB=%DLSS_SDK_DIR%\lib\Windows_x86_64\x64\nvsdk_ngx_d.lib"
  )
)
if not exist "%NGX_INCLUDE%\nvsdk_ngx.h" (
  echo NGX SDK headers not found. Put them in source\external\ngx or set DLSS_SDK_DIR to a DLSS SDK checkout.
  exit /b 1
)
if not exist "%NGX_LIB%" (
  echo NGX SDK import library not found: %NGX_LIB%
  exit /b 1
)
cl /nologo /O2 /EHsc /W3 /MD /I"%NGX_INCLUDE%" source\host\dlss5-feed-host64.cpp ^
   /Fe:runtime\host64\dlss5-feed-host64.exe ^
   /link "%NGX_LIB%" version.lib kernel32.lib user32.lib gdi32.lib advapi32.lib ole32.lib windowscodecs.lib
if errorlevel 1 exit /b 1
echo host built.
