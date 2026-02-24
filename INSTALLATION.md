# Installation

## Install via Plugin Repository (Recommended)

The easiest way to install is through Jellyfin's built-in plugin system:

1. Open Jellyfin → **Dashboard** → **Plugins** → **Repositories**
2. Click **+** to add a repository
3. **Repository Name:** `Hue Lighting Control`
4. **Repository URL:** `https://ericecook.github.io/JellyfinHuePlugin/manifest.json`
5. Click **Save**
6. Go to **Catalog**, find **Hue Lighting Control**, click **Install**
7. Restart Jellyfin
8. The plugin appears under **Dashboard** → **Plugins** — click it to configure

Updates will appear automatically in the Catalog when new versions are released.

---

## Manual Installation (Alternative)

Use this method if you're building from source or can't use the plugin repository.

### Step 1: Build the Plugin

```bash
cd JellyfinHuePlugin
dotnet build -c Release
```

### Step 2: Stop Jellyfin

**Windows:**
```cmd
net stop JellyfinServer
```

**Linux:**
```bash
sudo systemctl stop jellyfin
```

**Docker:**
```bash
docker stop jellyfin
```

### Step 3: Clean Old Installation (if exists)

Delete the old plugin folder completely:

| Platform | Path |
|----------|------|
| Windows | `C:\ProgramData\Jellyfin\Server\plugins\JellyfinHuePlugin\` |
| Linux | `/var/lib/jellyfin/plugins/JellyfinHuePlugin/` |
| Docker | `/config/plugins/JellyfinHuePlugin/` (inside container) |

### Step 4: Copy Plugin Files

Copy **everything** from `bin/Release/net8.0/` to the plugin directory above.

**Important:** Copy the entire folder contents, not just the `.dll` file. The folder should contain:
- `JellyfinHuePlugin.dll`
- `JellyfinHuePlugin.pdb`
- `JellyfinHuePlugin.deps.json`
- Multiple dependency DLLs

**Windows:**
```powershell
Copy-Item -Path "bin\Release\net8.0\*" -Destination "C:\ProgramData\Jellyfin\Server\plugins\JellyfinHuePlugin\" -Recurse
```

**Linux:**
```bash
sudo cp -r bin/Release/net8.0 /var/lib/jellyfin/plugins/JellyfinHuePlugin
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyfinHuePlugin
sudo chmod -R 755 /var/lib/jellyfin/plugins/JellyfinHuePlugin
```

**Docker:**
```bash
docker cp bin/Release/net8.0 jellyfin:/config/plugins/JellyfinHuePlugin
```

### Step 5: Start Jellyfin

**Windows:**
```cmd
net start JellyfinServer
```

**Linux:**
```bash
sudo systemctl start jellyfin
```

**Docker:**
```bash
docker start jellyfin
```

### Step 6: Verify

1. Open Jellyfin in your browser
2. Go to **Dashboard** → **Plugins**
3. Look for **Hue Lighting Control** in the list
4. Click on it to configure

If the plugin doesn't appear, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
