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
```

Copy the **entire** `bin/Release/net10.0/` folder contents, not just the main DLL.

### Check .NET Runtime

The plugin requires **.NET 10.0** and Jellyfin **12.0** or higher. Jellyfin 10.11 users should stay on plugin v2.2.0.0.

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
5. Copy entire `bin/Release/net10.0/` folder
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
- **Bridge unreachable**: Make sure the Jellyfin server can reach the bridge IP over HTTPS (port 443)
- **Bridge too old**: the log says `Bridge {Host} is not a supported v2 bridge (software {Version})`. Update the bridge in the Hue app; software 1948086000 or newer is required. The round v1 bridge cannot be updated and is not supported
- **Certificate rejected**: see the next section

---

## Bridge Certificate Rejected

Every request to a bridge is made over HTTPS and the bridge's certificate must chain to
Signify's root CA and name the bridge's id. A rejection is logged as:

```
Bridge certificate rejected for {Host}: {Verdict} (subject {Subject}, expected {BridgeId})
```

- **`UntrustedChain`**: whatever answered at that address did not present a Signify-issued
  certificate. Either it is not a Hue bridge (check the address), or something between
  Jellyfin and the bridge is intercepting TLS (a proxy or a "security" appliance).
- **`SubjectMismatch`**: a Hue bridge answered, but not the one this entry was paired with —
  typically the address now belongs to a different bridge after a DHCP change. Fix the
  address, or delete the entry and pair it again. The log names both ids.

The plugin never falls back to an unverified connection.

---

## Discover Finds Nothing

Discover asks the local network first (mDNS on `_hue._tcp.local`), then Philips' cloud
lookup. The log says which path answered:

```
Found {Count} Hue bridge(s) via mDNS
No Hue bridge answered mDNS; trying cloud discovery
```

- **Jellyfin in Docker** with the default bridge networking cannot send multicast to your
  LAN, so mDNS finds nothing there; the cloud lookup then works as long as the bridge has
  internet access. Host networking (`network_mode: host`) makes mDNS work.
- **The cloud lookup** only knows bridges that have contacted Philips recently.
- Either way you can type the bridge's IP address into the Add Bridge dialog.

---

## Profile Shows "Unresolved"

The profile editor lists the rooms, zones and scenes the bridge has now. A profile target the
bridge does not have — deleted in the Hue app, or an id stored by a version before 4.0 that was
never converted — is kept as `Unresolved (…) — re-select`. Open the profile and pick the room,
zone or scene again.

For an id from before 4.0, playback and Test log:

```
Profile target {Field} '{Value}' not found on bridge {BridgeName}; re-select it on the plugin page
```

A room or scene deleted in the Hue app shows up as a `Bridge returned {Status} for ...` warning
instead — the bridge answers a command against an id it no longer has with a non-2xx status,
typically 404 (see below). `Bridge error for ...` warnings are a different case: a 2xx response
that carries its own error list. Settings from before 4.0 are converted only by 4.0.0.0; see
**Upgrading from a version before 4.0** in the README.

---

## Lights Don't Change During Playback

- Verify the plugin is enabled
- Check that your profile's client/device/IP filters match your playback device
- Use **Test** on the Play, Pause or Stop section of the profile editor (it uses the values on screen, saved or not), or Test Play / Test Pause / Test Stop in the profile card's menu. A failed test says why under the button
- Check Jellyfin logs for profile matching debug messages
- Look for `Bridge error for ...` and `Bridge returned {Status} for ...` warnings from
  `HueService` in the Jellyfin log. A rejected parameter — a 2xx response that still carries
  an error — shows as `Bridge error for ...` with the bridge's own error description; a
  revoked API key (`unauthorized user`) or an unknown room, zone or scene id fails the whole
  request with a non-2xx status and shows as `Bridge returned {Status} for ...` instead
- `Could not reach bridge {BridgeName} to resolve profile target ...` means the bridge was
  offline or its key was revoked when a playback event arrived

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
