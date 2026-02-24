#!/bin/bash

echo "========================================"
echo "Jellyfin Hue Plugin - Build and Test"
echo "========================================"
echo ""

echo "[1/3] Building plugin..."
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
echo "[2/3] Running unit tests..."
cd ../JellyfinHuePlugin.Tests || exit 1
dotnet test --verbosity normal --nologo

if [ $? -ne 0 ]; then
    echo ""
    echo "========================================"
    echo "TESTS FAILED!"
    echo "========================================"
    exit 1
fi

echo ""
echo "[3/3] Creating distribution package..."
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
echo "Tests: PASSED"
echo "Package: JellyfinHuePlugin.zip"
echo "========================================"
echo ""
