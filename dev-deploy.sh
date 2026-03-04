#!/usr/bin/env bash
set -euo pipefail

PLUGIN_DIR="/srv/jellyfin/config/plugins/HueLightingControl"
PROJECT="JellyfinHuePlugin/JellyfinHuePlugin.csproj"

echo "Building plugin..."
dotnet publish "$PROJECT" -c Release -o ./dev-output

echo "Deploying to $PLUGIN_DIR..."
sudo mkdir -p "$PLUGIN_DIR"
sudo cp dev-output/* "$PLUGIN_DIR/"

echo "Restarting Jellyfin..."
docker compose restart jellyfin

echo ""
echo "Deployed! Access at http://localhost:8096"
