@echo off
echo ========================================
echo Jellyfin Hue Plugin - Build and Test
echo ========================================
echo.

echo [1/3] Building plugin...
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
echo [2/3] Running unit tests...
cd ..\JellyfinHuePlugin.Tests
dotnet test --verbosity normal --nologo

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ========================================
    echo TESTS FAILED!
    echo ========================================
    pause
    exit /b 1
)

echo.
echo [3/3] Creating distribution package...
cd ..
if exist JellyfinHuePlugin.zip del JellyfinHuePlugin.zip
powershell Compress-Archive -Path "JellyfinHuePlugin\bin\Release\net8.0\*" -DestinationPath "JellyfinHuePlugin.zip"

echo.
echo ========================================
echo SUCCESS!
echo ========================================
echo Build: PASSED
echo Tests: PASSED
echo Package: JellyfinHuePlugin.zip
echo ========================================
echo.
pause
