@echo off
title Trading Daemon
cd /d %~dp0daemon
echo ============================================
echo   Trading Daemon starting...
echo   Dashboard: http://localhost:8080
echo   Press Ctrl+C to stop
echo ============================================
echo.

:: Open dashboard in default browser after 3 seconds
start "" cmd /c "timeout /t 3 /nobreak >nul && start http://localhost:8080"

:: Run daemon (stays in foreground with console output)
dotnet run
