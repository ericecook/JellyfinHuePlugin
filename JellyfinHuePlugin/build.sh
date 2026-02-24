#!/bin/bash

# Jellyfin Hue Plugin Build Script

echo "Building Jellyfin Hue Plugin..."

# Clean previous builds
rm -rf bin/ obj/

# Build the plugin
dotnet build -c Release

if [ $? -eq 0 ]; then
    echo ""
    echo "Build successful!"
    echo ""
    echo "Installation instructions:"
    echo "1. Copy the contents of bin/Release/net8.0/ to your Jellyfin plugins directory:"
    echo "   - Linux: /var/lib/jellyfin/plugins/JellyfinHuePlugin/"
    echo "   - Windows: C:\\ProgramData\\Jellyfin\\Server\\plugins\\JellyfinHuePlugin\\"
    echo "   - Docker: /config/plugins/JellyfinHuePlugin/"
    echo ""
    echo "2. Restart Jellyfin"
    echo ""
    echo "3. Configure the plugin at Dashboard → Plugins → Hue Lighting Control"
else
    echo ""
    echo "Build failed! Please check the errors above."
    exit 1
fi
