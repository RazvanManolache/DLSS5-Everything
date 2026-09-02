@echo off
setlocal
if "%~1"=="" (
  echo Usage: run-smoke-tests.cmd path\to\smoke-tests.json
  exit /b 2
)
set "APP=%~dp0..\app\Dlss5CompatApp\bin\Release\net9.0-windows\win-x64\Dlss5CompatApp.exe"
if not exist "%APP%" set "APP=%~dp0..\app\Dlss5CompatApp\bin\Release\net9.0-windows\Dlss5CompatApp.exe"
if not exist "%APP%" (
  echo Build or publish Dlss5CompatApp first.
  exit /b 2
)
start "" /wait "%APP%" --smoke-test "%~1"
exit /b %ERRORLEVEL%
