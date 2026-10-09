@echo off
setlocal DisableDelayedExpansion
title Stepwise - First-time preparation
if not exist "%~dp0Stepwise.exe" goto missingApplication
if not exist "%~dp0tools\Get-FFmpeg.ps1" goto missingDownloader

echo Stepwise will download the pinned FFmpeg media tools from gyan.dev.
echo The download is verified with SHA-256. No administrator access is needed.
echo After this one-time preparation, Stepwise works offline.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Get-FFmpeg.ps1"
set "prepareExitCode=%ERRORLEVEL%"
if not "%prepareExitCode%"=="0" goto preparationFailed

echo.
echo Preparation completed. Starting Stepwise...
start "" "%~dp0Stepwise.exe"
set "prepareExitCode=%ERRORLEVEL%"
if not "%prepareExitCode%"=="0" goto launchFailed
exit /b 0

:missingApplication
set "prepareExitCode=2"
echo ERROR: Stepwise.exe was not found beside this file.
echo Extract the entire ZIP archive before running Prepare-Stepwise.cmd.
goto failed

:missingDownloader
set "prepareExitCode=3"
echo ERROR: tools\Get-FFmpeg.ps1 was not found.
echo Extract the entire ZIP archive again.
goto failed

:preparationFailed
echo.
echo ERROR: Media-tool preparation failed with exit code %prepareExitCode%.
echo Check the error above, your Internet connection, and folder write access.
echo Run Prepare-Stepwise.cmd again to retry. Stepwise has not been started.
goto failed

:launchFailed
echo ERROR: Stepwise could not be started. Exit code: %prepareExitCode%.
goto failed

:failed
echo.
pause
exit /b %prepareExitCode%
