' start_daemon.vbs - Launch trading daemon without console window
' Double-click this to start. Dashboard opens in browser after 3 seconds.
' Daemon runs in background. Shutdown from dashboard.

Set WshShell = CreateObject("WScript.Shell")
WshShell.Run "cmd /c cd /d D:\trading-daemon\daemon && dotnet run > bin\Debug\net8.0\Data\daemon.log 2>&1", 0, False
WScript.Sleep 3000
WshShell.Run "http://localhost:8080"
