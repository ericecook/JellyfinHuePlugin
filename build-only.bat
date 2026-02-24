@echo off
echo ========================================
echo Jellyfin Hue Plugin - Build Only
echo (Skipping Tests)
echo ========================================
echo.

echo [1/2] Building plugin...
cd JellyfinHuePlugin
dotnet build -c Release

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ========================================
    echo BUILD FAILED!
    echo ========================================
    pause
    exit /b 1
)

echo.
echo [2/2] Creating distribution package...
cd ..
if exist JellyfinHuePlugin.zip del JellyfinHuePlugin.zip
powershell Compress-Archive -Path "JellyfinHuePlugin\bin\Release\net8.0\*" -DestinationPath "JellyfinHuePlugin.zip"

echo.
echo ========================================
echo SUCCESS!
echo ========================================
echo Build: PASSED
echo Tests: SKIPPED
echo Package: JellyfinHuePlugin.zip
echo ========================================
echo.
echo Ready to install!
echo See INSTALLATION.md for instructions.
echo.
pause
