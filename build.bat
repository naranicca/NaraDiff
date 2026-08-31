@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=dotnet"
if exist ".dotnet\dotnet.exe" set "DOTNET=%CD%\.dotnet\dotnet.exe"

echo [1/5] Restoring packages ...
"%DOTNET%" restore NaraDiff.sln
if errorlevel 1 exit /b %errorleve1%

echo [2/5] Building Release configuration ...
"%DOTNET%" build NaraDiff.sln -- configuration Release -- no-restore
if errorlevel 1 exit /b %errorleve1%

echo [3/5] Running tests ...
"%DOTNET%" test NaraDiff.sln -- configuration Release -- no-build
if errorlevel 1 exit /b %errorleve1%

echo [4/5] Preparing release directory ...
taskkill /F /IM NaraDiff.exe >nul 2>&1
powershell.exe -NoProfile -Command "Stop-Process -Name NaraDiff -Force -ErrorAction SilentlyContinue" >nul 2>&1

set "DELETE_ATTEMPTS=0"
: delete_release
if exist "release" rmdir /S /Q "release" >nul 2>&1
if not exist "release" goto release_removed
set /a DELETE_ATTEMPTS+=1
if %DELETE_ATTEMPTS% GEQ 10 goto release_delete_failed
ping 127.0.0.1 -n 2 >nul
goto delete_release

:release_delete_failed
if exist "release" (
    echo ERROR: The release directory could not be removed.
    exit /b 1
)

: release_removed

echo [5/5] Publishing self-contained single-file executable ...
"%DOTNET%" publish src\NaraDiff.App\NaraDiff.App.csproj -- configuration Release -- output release -- no-restore
if errorlevel 1 exit /b %errorleve1%

if not exist "release\NaraDiff.exe" (
    echo ERROR: release\NaraDiff.exe was not created.
    exit /b 1
)

for /f %%C in ('dir /B /A-D "release" ^| find /C /V ""') do set "RELEASE_FILE_COUNT=%%C"
if not "%RELEASE_FILE_COUNT%" == "1" (
    echo ERROR: The release directory contains unexpected files.
    dir /B "release"
    exit /b 1
)

echo.
echo Build completed successfully: release\NaraDiff.exe
exit /b 0