#!/usr/bin/env bash
set -euo pipefail

BUMP_TYPE="${1:-patch}"

# Read current version from build.yaml
CURRENT_VERSION=$(grep '^version:' build.yaml | sed 's/version: *"\(.*\)"/\1/')
IFS='.' read -r MAJOR MINOR PATCH BUILD <<< "$CURRENT_VERSION"

case "$BUMP_TYPE" in
    patch) PATCH=$((PATCH + 1)); BUILD=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0; BUILD=0 ;;
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0; BUILD=0 ;;
    *) echo "Usage: $0 [patch|minor|major]"; exit 1 ;;
esac

NEW_VERSION="${MAJOR}.${MINOR}.${PATCH}.${BUILD}"
TAG="v${NEW_VERSION}"

echo "Version: ${CURRENT_VERSION} -> ${NEW_VERSION}"
echo "Tag: ${TAG}"
echo ""
read -p "Proceed? [y/N] " -n 1 -r
echo ""
[[ $REPLY =~ ^[Yy]$ ]] || exit 0

# Update version in build.yaml
sed -i "s/^version: \".*\"/version: \"${NEW_VERSION}\"/" build.yaml

# Commit and tag
git add build.yaml
git commit -m "Release ${TAG}"
git tag "$TAG"

# Push — pauses for YubiKey touch
echo ""
echo "Pushing to origin (touch YubiKey when prompted)..."
git push origin main
echo "Pushing tag to origin (touch YubiKey when prompted)..."
git push origin "$TAG"

# Create GitHub release (triggers the release workflow)
echo ""
echo "Creating GitHub release..."
gh release create "$TAG" --title "$TAG" --generate-notes

echo ""
echo "Done! Release workflow will build and update the manifest."
