@echo off
setlocal
cd /d "%~dp0"
dotnet build AutomationPlatform.slnx --nologo
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b %errorlevel%
)
dotnet "Kes.Gui\bin\Debug\net9.0-windows10.0.19041.0\Kes.Gui.dll"