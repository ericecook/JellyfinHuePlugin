#!/bin/bash

echo "========================================"
echo "Jellyfin Hue Plugin - Build Only"
echo "(Skipping Tests)"
echo "========================================"
echo ""

echo "[1/2] Building plugin..."
cd JellyfinHuePlugin || exit 1
dotnet build -c Release

if [ $? -ne 0 ]; then
    echo ""
    echo "========================================"
    echo "BUILD FAILED!"
    echo "========================================"
    exit 1
fi

echo ""
echo "[2/2] Creating distribution package..."
cd ..
rm -f JellyfinHuePlugin.zip
cd JellyfinHuePlugin/bin/Release/net8.0 || exit 1
zip -r ../../../../JellyfinHuePlugin.zip ./*
cd ../../../..

echo ""
echo "========================================"
echo "SUCCESS!"
echo "========================================"
echo "Build: PASSED"
echo "Tests: SKIPPED"
echo "Package: JellyfinHuePlugin.zip"
echo "========================================"
echo ""
echo "Ready to install!"
echo "See INSTALLATION.md for instructions."
echo ""
