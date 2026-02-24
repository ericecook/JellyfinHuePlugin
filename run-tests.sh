#!/bin/bash

echo "Running Jellyfin Hue Plugin Tests..."
echo ""

cd JellyfinHuePlugin.Tests || exit 1
dotnet test --verbosity normal

echo ""
if [ $? -eq 0 ]; then
    echo "All tests passed! ✓"
else
    echo "Some tests failed! ✗"
fi
echo ""
