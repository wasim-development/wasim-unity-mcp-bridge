@echo off
setlocal
if "%~1"=="" (
  echo Usage: start-companion.bat "FULL_PATH_TO_companion-config.json"
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0wdmcp-companion.ps1" -ConfigPath "%~1"
