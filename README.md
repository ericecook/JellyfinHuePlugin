# Jellyfin Hue Plugin

A Jellyfin plugin that automatically controls Philips Hue lights based on media playback events.

## Features

- **Multi-Bridge Support** — configure and control multiple Hue bridges independently
- **Automatic Bridge Discovery** — finds Hue bridges via cloud lookup or local SSDP
- **Multi-Profile System** — configure different light behaviors for different rooms/devices
- **Media Type Filtering** — separate rules for movies vs TV shows
- **Client Filtering** — target by client name, device ID, or IP address
- **Scene Support** — activate Hue scenes for play/pause/stop states
- **Brightness Control** — dim lights on play, brighten on pause, restore on stop (0-254)
- **Turn Off Lights** — optionally turn lights off completely during playback
- **Smooth Transitions** — configurable transition duration (0-15 seconds)
- **Pause Grace Period** — skip pause lighting during the first N seconds of playback
- **Outro Detection** — raise lights when credits start (uses Jellyfin Media Segments API)
- **Profile Templates** — 4 presets: Movie Theater, TV Viewing, Bedroom Casual, Gaming

## Requirements

- Jellyfin 10.11+
- .NET 9.0
- Philips Hue Bridge (v2)
- For outro detection: a media segment provider such as [IntroSkipper](https://github.com/intro-skipper/intro-skipper)

## Installation

### Plugin Repository (Recommended)

1. Open Jellyfin → **Dashboard** → **Plugins** → **Repositories**
2. Click **+** to add a repository
3. **Name:** `Hue Lighting Control`
4. **URL:** `https://ericecook.github.io/JellyfinHuePlugin/manifest.json`
5. Click **Save**, go to **Catalog**, find **Hue Lighting Control**, click **Install**
6. Restart Jellyfin

### Manual Installation

See [INSTALLATION.md](INSTALLATION.md) for building from source and manual install on Linux, Windows, and Docker.

## Configuration

### Initial Setup

1. Go to **Dashboard > Plugins > Hue Lighting Control**
2. Click **Add Bridge**, then click **Discover** to find your bridge
3. Press the link button on your Hue bridge, then click **Authenticate**
4. Create a profile and configure your light settings

### Profiles

Each profile defines how lights behave for a specific playback context. Profiles are matched in order — the first match wins.

**Profile settings:**
- **Bridge**: Which Hue bridge this profile uses
- **Media types**: Enable for Movies, TV Shows, or both
- **Filters**: Target by client name (substring), device IDs (exact), or IP address
- **Light group**: Which Hue group to control
- **Play state**: Activate a scene, dim to a brightness level, or turn off completely
- **Pause state**: Activate a scene or brighten to a level
- **Stop state**: Activate a scene or restore to a level
- **Transition duration**: How quickly lights change (0-15 seconds)
- **Pause grace period**: Skip pause lighting during the first N seconds of playback
- **Outro detection**: Trigger stop-state lights when credits begin

### Light Control Priority

When playback starts, the plugin decides what to do in this order:
1. If a **Play Scene** is set — activate the scene
2. Else if **Turn Off Lights** is checked — turn lights off
3. Else — dim to the configured **Play Brightness**

### Example: Multi-Room Setup

| Profile | Bridge | Filter | Group | Play | Pause | Stop |
|---------|--------|--------|-------|------|-------|------|
| Theater | Main | Device ID: `firetv-theater-123` | Theater | OFF | Brightness 30 | Brightness 254 |
| Living Room | Main | Client: `Roku` | Living Room | Brightness 10 | Brightness 100 | Brightness 254 |
| Bedroom | Upstairs | IP: `192.168.1.50` | Bedroom | Scene: "Nightlight" | Scene: "Relax" | Scene: "Bright" |

Put more specific profiles first (device ID > client name > no filter).

## API Endpoints

All endpoints require Jellyfin authentication.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/hueplugin/discover` | GET | Discover Hue bridges |
| `/api/hueplugin/bridges` | GET | List configured bridges |
| `/api/hueplugin/bridges` | POST | Add a new bridge |
| `/api/hueplugin/bridges/{bridgeId}` | DELETE | Delete a bridge |
| `/api/hueplugin/authenticate` | POST | Authenticate with bridge |
| `/api/hueplugin/lights` | GET | List all lights |
| `/api/hueplugin/groups` | GET | List all groups |
| `/api/hueplugin/scenes` | GET | List all scenes |
| `/api/hueplugin/test` | POST | Test light control |
| `/api/hueplugin/testconnection` | POST | Test bridge connectivity |

## Architecture

```
Plugin.cs                             Entry point and lifecycle
├── Services/HueService.cs            Hue Bridge HTTP API client
├── Managers/PlaybackSessionManager.cs Playback events and light orchestration
├── Configuration/PluginConfiguration.cs Settings models
├── Configuration/configPage.html      Web configuration UI
├── Api/HueController.cs              REST API for the config UI
└── Models/                           Hue API data models
```

## Troubleshooting

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for common issues including:
- Plugin won't load
- Bridge authentication fails
- Lights don't respond
- Finding device IDs
