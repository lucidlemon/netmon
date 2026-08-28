@echo off
REM Rebuilds NetMonGui and drops a standalone NetMonGui.exe next to this script.
REM Requires the .NET SDK (already installed if you have Visual Studio's
REM ".NET desktop development" workload).

cd /d "%~dp0NetMonGui"
dotnet publish -c Release
if errorlevel 1 goto :error

copy /Y "bin\Release\net10.0-windows\win-x64\publish\NetMonGui.exe" "%~dp0NetMonGui.exe"
echo.
echo Done: %~dp0NetMonGui.exe
pause
goto :eof

:error
echo.
echo Build failed.
pause
