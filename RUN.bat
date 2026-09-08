@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "EXE=src\DatesErp.Desktop\bin\Release\net8.0-windows\win-x64\DateERP.exe"
if not exist "%EXE%" set "EXE=src\DatesErp.Desktop\bin\Release\net8.0-windows\DateERP.exe"

REM ---- already built? just launch ----
if exist "%EXE%" goto :launch

echo.
echo  First run - building the app (5-10 minutes). Please wait...
echo.
where dotnet >nul 2>&1 || (
    echo  [X] .NET 8 SDK not found.
    echo      Download: https://dotnet.microsoft.com/download/dotnet/8.0
    pause & exit /b 1
)
dotnet build DateERP.sln -c Release
if errorlevel 1 (
    echo.
    echo  [X] Build failed. Run tools\build\build.bat to collect errors.
    pause & exit /b 1
)

set "EXE=src\DatesErp.Desktop\bin\Release\net8.0-windows\win-x64\DateERP.exe"
if not exist "%EXE%" set "EXE=src\DatesErp.Desktop\bin\Release\net8.0-windows\DateERP.exe"
if not exist "%EXE%" (
    echo  [X] Build finished but DateERP.exe was not found.
    pause & exit /b 1
)

:launch
start "" "%EXE%"
exit /b 0
