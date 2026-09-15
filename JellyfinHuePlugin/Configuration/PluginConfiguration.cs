using MediaBrowser.Model.Plugins;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace JellyfinHuePlugin.Configuration
{
    public class HueBridge
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// The bridge's own id, as reported by Hue's <c>bridgeid</c> field from
        /// <c>GET /api/0/config</c>: 16 lower-case hex characters, and the subject Common Name
        /// of its TLS certificate. Not to be confused with <see cref="Id"/>, the GUID this
        /// plugin generates to identify a configured bridge entry. Empty until learned.
        /// Stored under the element name "BridgeId", which it had before the property was
        /// renamed; changing the element name would need a conversion of every stored file.
        /// </summary>
        [XmlElement("BridgeId")]
        public string HardwareId { get; set; } = string.Empty;
    }

    public class LightControlProfile
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "Default Profile";
        public bool Enabled { get; set; } = true;

        // Bridge reference
        public string BridgeId { get; set; } = string.Empty;

        // Media type filters
        public bool EnableForMovies { get; set; } = true;
        public bool EnableForTvShows { get; set; } = false;

        // Client filters
        public string TargetClientName { get; set; } = string.Empty;
        public List<string> TargetDeviceIds { get; set; } = new List<string>();
        public string TargetIpAddress { get; set; } = string.Empty;

        // Light control settings
        public string TargetGroupId { get; set; } = "0";

        // Scenes
        public string PlaySceneId { get; set; } = string.Empty;
        public string PauseSceneId { get; set; } = string.Empty;
        public string StopSceneId { get; set; } = string.Empty;

        // Brightness, percent (0–100)
        public bool TurnOffLightsOnPlay { get; set; } = false;
        public int PlayBrightness { get; set; } = 8;
        public int PauseBrightness { get; set; } = 39;
        public int StopBrightness { get; set; } = 100;

        // Per-state transition settings (in deciseconds, 0.1s increments)
        public bool EnablePlayTransition { get; set; } = false;
        public int PlayTransitionDuration { get; set; } = 4;
        public bool EnablePauseTransition { get; set; } = false;
        public int PauseTransitionDuration { get; set; } = 4;
        public bool EnableStopTransition { get; set; } = false;
        public int StopTransitionDuration { get; set; } = 4;

        // Pause grace period — skip pause actions during the first N seconds of playback
        public int PauseGracePeriodSeconds { get; set; } = 0;

        // Outro detection
        public bool EnableOutroLights { get; set; } = false;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<HueBridge> Bridges { get; set; } = new List<HueBridge>();

        public List<LightControlProfile> Profiles { get; set; } = new List<LightControlProfile>();

        public bool EnablePlugin { get; set; } = true;

        /// <summary>
        /// Version of the stored shape. Every file written since 4.0 carries it. A file without the
        /// element predates 4.0 and reads as current: nothing converts those any more, and 4.0.0.0 is
        /// the upgrade path. The next change to the stored shape bumps <see cref="CurrentSchemaVersion"/>
        /// and converts from the stored value.
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// The bridge a profile controls: an explicit BridgeId must match; an empty one means the
        /// only bridge, if there is exactly one. Playback and the Test endpoint both use this, so
        /// Test sends where playback would.
        /// </summary>
        public HueBridge? FindProfileBridge(LightControlProfile profile) =>
            string.IsNullOrWhiteSpace(profile.BridgeId)
                ? (Bridges.Count == 1 ? Bridges[0] : null)
                : Bridges.FirstOrDefault(b => b.Id == profile.BridgeId);

        /// <summary>
        /// Drops entries no caller should store - a <c>null</c> bridge, profile or device id, which
        /// any client posting the configuration can send - and replaces missing lists with empty
        /// ones, so every consumer can iterate without null checks. Returns how many entries were
        /// removed plus lists replaced; 0 means nothing changed.
        /// </summary>
        public int RemoveInvalidEntries()
        {
            var repaired = 0;
            if (Bridges == null)
            {
                Bridges = new List<HueBridge>();
                repaired++;
            }

            if (Profiles == null)
            {
                Profiles = new List<LightControlProfile>();
                repaired++;
            }

            repaired += Bridges.RemoveAll(b => b == null);
            repaired += Profiles.RemoveAll(p => p == null);
            foreach (var profile in Profiles)
            {
                if (profile.TargetDeviceIds == null)
                {
                    profile.TargetDeviceIds = new List<string>();
                    repaired++;
                }

                repaired += profile.TargetDeviceIds.RemoveAll(id => id == null);
            }

            return repaired;
        }
    }
}
