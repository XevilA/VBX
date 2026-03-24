@echo off
setlocal enabledelayedexpansion

echo.
echo ===========================================================
echo   VBX DISPENSING CONTROL - Standalone Build
echo   Target: Windows x64 Single-File Self-Contained EXE
echo ===========================================================
echo.

cd /d "%~dp0"

set "PROJ=DispensingControlSystem.vbproj"
set "OUT=Output\Standalone\win-x64"

echo [1/3] Cleaning previous build...
echo        Stopping running instances...
taskkill /f /im DispensingControlSystem.exe >nul 2>&1
timeout /t 2 /nobreak >nul
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [2/3] Publishing self-contained single-file EXE...
echo        Runtime: win-x64
echo        Config:  Release
echo.

dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:PublishTrimmed=false -o "%OUT%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo   BUILD FAILED [exit code: %ERRORLEVEL%]
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/3] Verifying output...
for %%F in ("%OUT%\DispensingControlSystem.exe") do (
    echo        EXE:  %%~nxF
    echo        Size: %%~zF bytes
    echo        Path: %%~fF
)

echo.
echo ===========================================================
echo   BUILD SUCCESS
echo   Output: %OUT%
echo ===========================================================
echo.
echo   Run:  %OUT%\DispensingControlSystem.exe
echo.

pause
