@echo off
setlocal
set "APP_DIR=%~dp0publish\win-x64"

if not exist "%APP_DIR%\Wavy.exe" (
    echo Wavy 1.8 was not found in:
    echo "%APP_DIR%"
    echo Keep this script in the WavyMusic folder alongside the publish folder.
    pause
    exit /b 1
)

start "" /D "%APP_DIR%" "%APP_DIR%\Wavy.exe"
exit /b %errorlevel%
