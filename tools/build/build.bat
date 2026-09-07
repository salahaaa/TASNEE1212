@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0..\.."

set "LOG=%~dp0full-log.txt"
set "OUT=%~dp0RESULT.txt"

echo.
echo ==============================================
echo    DateERP - Build and Test
echo ==============================================
echo    Folder: %CD%
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [X] .NET SDK not found.
    echo     Install .NET 8 SDK from:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "SDKVER=%%v"
echo    [1/4] SDK version: !SDKVER!

if not exist "DateERP.sln" (
    echo.
    echo [X] DateERP.sln NOT FOUND in: %CD%
    echo     This bat must sit inside ^<project^>\tools\build\
    echo.
    pause
    exit /b 1
)
echo    [1/4] DateERP.sln found - OK

echo    [2/4] Restoring packages... (5-10 min on first run)
echo ===== RESTORE ===== > "%LOG%"
dotnet restore DateERP.sln >> "%LOG%" 2>&1

echo    [3/4] Building (Release)...
echo. >> "%LOG%"
echo ===== BUILD ===== >> "%LOG%"
dotnet build DateERP.sln -c Release --no-restore >> "%LOG%" 2>&1
set "RC=!errorlevel!"

if "!RC!"=="0" (
    echo    [4/4] Build OK - running tests...
    echo. >> "%LOG%"
    echo ===== TESTS ===== >> "%LOG%"
    dotnet test tests\DatesErp.Tests\DatesErp.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1
) else (
    echo    [4/4] Build FAILED - skipping tests.
)

> "%OUT%" echo DateERP build result
>> "%OUT%" echo Date: %DATE% %TIME%
>> "%OUT%" echo SDK: !SDKVER!
>> "%OUT%" echo BuildExitCode: !RC!
>> "%OUT%" echo.
>> "%OUT%" echo ==================== ERRORS ====================
findstr /C:": error " "%LOG%" >> "%OUT%" 2>nul
>> "%OUT%" echo.
>> "%OUT%" echo ==================== SUMMARY ====================
findstr /C:"Build succeeded" /C:"Build FAILED" /C:"Warning(s)" /C:"Error(s)" /C:"Passed!" /C:"Failed!" /C:"Total tests" "%LOG%" >> "%OUT%" 2>nul
>> "%OUT%" echo.
>> "%OUT%" echo ==================== FAILED TESTS ====================
findstr /C:"[FAIL]" "%LOG%" >> "%OUT%" 2>nul

echo.
echo ==============================================
if "!RC!"=="0" (echo    RESULT: BUILD OK) else (echo    RESULT: BUILD FAILED)
echo.
echo    Send me this file:
echo    %OUT%
echo ==============================================
echo.
if exist "%OUT%" (
    start "" notepad "%OUT%"
) else (
    echo [X] RESULT.txt was not created. See: %LOG%
)
pause
