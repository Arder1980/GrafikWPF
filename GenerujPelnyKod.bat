@echo off
setlocal ENABLEDELAYEDEXPANSION

set "SCRIPT=%~dp0ExportProject.ps1"

if not exist "%SCRIPT%" (
  echo [BLAD] Nie znaleziono skryptu: "%SCRIPT%"
  pause
  exit /b 1
)

rem Preferuj PowerShell 7 (pwsh), ale w razie czego uzyj Windows PowerShell (powershell.exe)
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
)

if errorlevel 1 (
  echo.
  echo [BLAD] Wystapil problem podczas generowania pliku TXT.
  pause
) else (
  echo.
  echo Gotowe. Plik TXT z pelnym kodem zostal utworzony w katalogu projektu.
  timeout /t 2 >nul
)
