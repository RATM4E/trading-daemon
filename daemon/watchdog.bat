@echo off
:: ============================================================
:: Trading Daemon Watchdog
:: Run via Task Scheduler every 2 minutes
:: If daemon is not running AND last shutdown was unclean - restart
:: ============================================================

set DAEMON_DIR=D:\trading-daemon\daemon
set DATA_DIR=%DAEMON_DIR%\bin\Debug\net8.0\Data
set FLAG_FILE=%DATA_DIR%\.clean_shutdown
set LOG_FILE=%DATA_DIR%\watchdog.log

:: Ensure Data directory exists
if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"

:: If clean shutdown flag exists - daemon was stopped intentionally, do nothing
if exist "%FLAG_FILE%" (
    exit /b 0
)

:: Check if dotnet daemon is already running
tasklist /FI "IMAGENAME eq dotnet.exe" 2>NUL | find /I "dotnet.exe" >NUL
if %ERRORLEVEL%==0 (
    wmic process where "name='dotnet.exe'" get CommandLine 2>NUL | find /I "trading-daemon" >NUL
    if %ERRORLEVEL%==0 (
        exit /b 0
    )
)

:: Daemon is NOT running and no clean shutdown flag - restart
echo [%DATE% %TIME%] Daemon not found, restarting... >> "%LOG_FILE%"

cd /d %DAEMON_DIR%
echo Set WS = CreateObject("WScript.Shell") > "%TEMP%\_restart.vbs"
echo WS.Run "cmd /c cd /d %DAEMON_DIR% && dotnet run >> bin\Debug\net8.0\Data\daemon_output.log 2>&1", 0, False >> "%TEMP%\_restart.vbs"
wscript "%TEMP%\_restart.vbs"

echo [%DATE% %TIME%] Daemon restarted >> "%LOG_FILE%"
