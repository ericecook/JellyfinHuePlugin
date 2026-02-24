@echo off

echo Building Jellyfin Hue Plugin...
echo.

REM Clean previous builds
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

REM Build the plugin
dotnet build -c Release

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Build successful!
    echo.
    echo Installation instructions:
    echo 1. Copy the contents of bin\Release\net8.0\ to:
    echo    C:\ProgramData\Jellyfin\Server\plugins\JellyfinHuePlugin\
    echo.
    echo 2. Restart Jellyfin
    echo.
    echo 3. Configure the plugin at Dashboard - Plugins - Hue Lighting Control
    echo.
) else (
    echo.
    echo Build failed! Please check the errors above.
    exit /b 1
)

pause
