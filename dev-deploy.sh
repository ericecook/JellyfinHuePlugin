#!/usr/bin/env bash
set -euo pipefail

PLUGIN_DIR="/srv/jellyfin/config/plugins/HueLightingControl"
PROJECT="JellyfinHuePlugin/JellyfinHuePlugin.csproj"

# build.yaml is the only place the version lives; the csproj has none, so an unstamped
# build loads as 1.0.0.0. Stamp it the way release.yml does.
VERSION=$(grep '^version:' build.yaml | sed 's/version: *"\(.*\)"/\1/')

echo "Building plugin ${VERSION}..."
dotnet publish "$PROJECT" -c Release -o ./dev-output \
    -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

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
