@echo off
setlocal
cd /d "%~dp0"
echo  Updating from GitHub...
git pull
if errorlevel 1 echo  [!] git pull failed - continuing with current version.
echo  Building...
dotnet build DateERP.sln -c Release
if errorlevel 1 (echo  [X] Build failed. & pause & exit /b 1)
call "%~dp0RUN.bat"
