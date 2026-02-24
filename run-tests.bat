@echo off
echo Running Jellyfin Hue Plugin Tests...
echo.

cd JellyfinHuePlugin.Tests
dotnet test --verbosity normal

echo.
if %ERRORLEVEL% EQU 0 (
    echo All tests passed! ✓
) else (
    echo Some tests failed! ✗
)
echo.
pause
