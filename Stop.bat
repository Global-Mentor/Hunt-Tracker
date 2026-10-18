@echo off
cd /d "%~dp0"
if exist "%~dp0app\HuntTracker.exe" (
    start "" /wait "%~dp0app\HuntTracker.exe" --root "%~dp0." --stop
    exit /b
)
if exist "%~dp0.local\build\HuntTracker.exe" start "" /wait "%~dp0.local\build\HuntTracker.exe" --root "%~dp0." --stop
