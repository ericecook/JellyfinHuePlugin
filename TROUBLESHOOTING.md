# Troubleshooting

## Plugin Won't Load

### Check Jellyfin Logs

**Windows**: `C:\ProgramData\Jellyfin\Server\log\`
**Linux**: `/var/log/jellyfin/`

Search for `JellyfinHuePlugin`, `Hue`, `error`, or `exception`.

### Verify Installation Location

The plugin files should be in:
- **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\JellyfinHuePlugin\`
- **Linux**: `/var/lib/jellyfin/plugins/JellyfinHuePlugin/`

Required files:
```
JellyfinHuePlugin/
  JellyfinHuePlugin.dll
  JellyfinHuePlugin.pdb
  JellyfinHuePlugin.deps.json
  (dependency DLLs)
```

Copy the **entire** `bin/Release/net9.0/` folder contents, not just the main DLL.

### Check .NET Runtime

The plugin requires **.NET 9.0** and Jellyfin **10.11** or higher.

### Check File Permissions (Linux)

```bash
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyfinHuePlugin/
sudo chmod -R 755 /var/lib/jellyfin/plugins/JellyfinHuePlugin/
```

### Quick Reset

1. Stop Jellyfin
2. Delete the plugin folder completely
3. Delete configuration: `C:\ProgramData\Jellyfin\Server\data\plugins\configurations\JellyfinHuePlugin.xml`
4. Rebuild the plugin fresh
5. Copy entire `bin/Release/net9.0/` folder
6. Start Jellyfin

---

## Bridge Authentication Fails

### Correct Button Press Procedure

1. Locate the **large round button on top** of the Hue bridge (not the reset button on the back)
2. **Press the button** — it will light up briefly
3. **Within 30 seconds**, click "Authenticate" in the plugin settings
4. The plugin retries 3 times automatically (3 seconds apart)

You can also click "Authenticate" first and then press the bridge button — the retries give you time to walk over and press it.

### Common Authentication Mistakes

- **Wrong button**: Use the large button on top, not the small reset button on the back
- **Waiting too long**: The 30-second auth window expires — press the button and authenticate promptly
- **Bridge unreachable**: Make sure the Jellyfin server can reach the bridge IP over HTTPS

---

## Lights Don't Change During Playback

- Verify the plugin is enabled
- Check that your profile's client/device/IP filters match your playback device
- Test the light control using the "Test" button in the profile editor
- Check Jellyfin logs for profile matching debug messages

---

## Finding Your Device ID

Device IDs let you target a specific device (e.g., only your living room Roku, not the bedroom one).

### Method 1: Check Jellyfin Logs

1. Start playback on the target device
2. Check logs for: `Playback started on {ClientName} (Device: {DeviceId}, IP: {IP})`
3. Copy the Device ID value into your profile's device ID list

### Method 2: Jellyfin API

Open browser console (F12) while Jellyfin is open:
```javascript
fetch('/Sessions')
  .then(r => r.json())
  .then(sessions => {
    sessions.forEach(s => {
      if (s.NowPlayingItem) {
        console.log(`Client: ${s.Client}, Device: ${s.DeviceId}`);
      }
    });
  });
```

### Device ID vs Client Name

- **Client Name** (substring match): Matches all devices of a type (e.g., "Roku" matches every Roku)
- **Device ID** (exact match): Targets one specific device
- **Both set**: Device must match all filters (AND logic)
- **Both empty**: Matches all playback
