@echo off
setlocal
if not exist "%~dp0Camera-Controls.exe" (
    echo Camera-Controls.exe is missing. Extract the complete prototype package first.
    pause
    exit /b 1
)
start "Lenovo Camera Controls" /wait "%~dp0Camera-Controls.exe"
if errorlevel 1 (
    echo Camera Controls could not start. Check any error message displayed above.
    pause
    exit /b 1
)
