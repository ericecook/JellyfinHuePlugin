#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<USAGE
Usage: $0 [patch|minor|major] [NOTES_FILE]

Bumps the version in build.yaml, commits, tags, pushes, and creates the GitHub
release. The release notes become the plugin changelog in the published
manifest (the manifest action reads the release body, not build.yaml), so
notes are required: pass a Markdown file, or omit it to write them in \$EDITOR.
USAGE
}

BUMP_TYPE="${1:-patch}"
NOTES_FILE="${2:-}"

case "$BUMP_TYPE" in
    patch|minor|major) ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 1 ;;
esac

# Read current version from build.yaml
CURRENT_VERSION=$(grep '^version:' build.yaml | sed 's/version: *"\(.*\)"/\1/')
IFS='.' read -r MAJOR MINOR PATCH BUILD <<< "$CURRENT_VERSION"

case "$BUMP_TYPE" in
    patch) PATCH=$((PATCH + 1)); BUILD=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0; BUILD=0 ;;
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0; BUILD=0 ;;
esac

NEW_VERSION="${MAJOR}.${MINOR}.${PATCH}.${BUILD}"
TAG="v${NEW_VERSION}"

# Release notes, collected before anything is changed
if [[ -z "$NOTES_FILE" ]]; then
    NOTES_FILE=$(mktemp -t release-notes-XXXXXX.md)
    trap 'rm -f "$NOTES_FILE"' EXIT
    echo "Write the release notes for ${TAG} (Markdown). They are published as the"
    echo "GitHub release body and shown as the changelog in Jellyfin's plugin catalog."
    echo "Save an empty file to abort."
    # shellcheck disable=SC2086 -- EDITOR may carry flags, e.g. "code --wait"
    ${EDITOR:-vi} "$NOTES_FILE"
elif [[ ! -r "$NOTES_FILE" ]]; then
    echo "Notes file not readable: $NOTES_FILE" >&2
    exit 1
fi

if ! grep -q '[^[:space:]]' "$NOTES_FILE"; then
    echo "Release notes are empty; nothing changed." >&2
    exit 1
fi

echo "Version: ${CURRENT_VERSION} -> ${NEW_VERSION}"
echo "Tag: ${TAG}"
echo ""
echo "Release notes:"
echo "----------------------------------------"
cat "$NOTES_FILE"
echo "----------------------------------------"
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
gh release create "$TAG" --title "$TAG" --notes-file "$NOTES_FILE"

echo ""
echo "Done! Release workflow will build and update the manifest."
