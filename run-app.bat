@echo off
setlocal
set "APP_DIR=%~dp0publish\win-x64"
set "BUILD_SCRIPT=%~dp0tools\build-release.ps1"
set "REBUILD_FLAG=%~dp0publish\.rebuild-required"

if /I "%~1"=="--build" goto build
if exist "%REBUILD_FLAG%" goto build
if not exist "%APP_DIR%\Wavy.exe" goto build
if not exist "%APP_DIR%\App.xbf" goto build
goto launch

:build
echo Building Wavy...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BUILD_SCRIPT%"
if errorlevel 1 (
    echo Wavy could not be built. Close Wavy if it is running, then try again.
    pause
    exit /b 1
)
if exist "%REBUILD_FLAG%" del /q "%REBUILD_FLAG%"

:launch
start "" /D "%APP_DIR%" "%APP_DIR%\Wavy.exe"
exit /b %errorlevel%
