#!/usr/bin/env bash
set -euo pipefail

PLUGIN_DIR="/srv/jellyfin/config/plugins/HueLightingControl"
PROJECT="JellyfinHuePlugin/JellyfinHuePlugin.csproj"

echo "Building plugin..."
dotnet publish "$PROJECT" -c Release -o ./dev-output

# Stop before copying: replacing the DLL under a running server makes its shutdown log a
# BadImageFormatException from the plugin's dispose path.
echo "Stopping Jellyfin..."
docker compose stop jellyfin

echo "Deploying to $PLUGIN_DIR..."
mkdir -p "$PLUGIN_DIR"
cp dev-output/* "$PLUGIN_DIR/"

echo "Starting Jellyfin..."
docker compose start jellyfin

echo ""
echo "Deployed! Access at http://localhost:8096"
