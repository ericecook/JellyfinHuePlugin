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

        // Brightness
        public bool TurnOffLightsOnPlay { get; set; } = false;
        public int PlayBrightness { get; set; } = 20;
        public int PauseBrightness { get; set; } = 100;
        public int StopBrightness { get; set; } = 254;

        // Per-state transition settings (in deciseconds, 0.1s increments)
        public bool EnablePlayTransition { get; set; } = false;
        public int PlayTransitionDuration { get; set; } = 4;
        public bool EnablePauseTransition { get; set; } = false;
        public int PauseTransitionDuration { get; set; } = 4;
        public bool EnableStopTransition { get; set; } = false;
        public int StopTransitionDuration { get; set; } = 4;

        // Backward compat: old configs had a single TransitionDuration property.
        // When deserialized, apply it to all three per-state durations.
        [XmlElement("TransitionDuration")]
        public int LegacyTransitionDuration
        {
            get => 0; // never serialize this
            set
            {
                PlayTransitionDuration = value;
                PauseTransitionDuration = value;
                StopTransitionDuration = value;
                EnablePlayTransition = true;
                EnablePauseTransition = true;
                EnableStopTransition = true;
            }
        }
        public bool ShouldSerializeLegacyTransitionDuration() => false;

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

        // Legacy single-bridge fields — absorbed on deserialization, never written back.
        // MigrateLegacyConfig() converts them to a Bridges entry.
        [XmlElement("BridgeIpAddress")]
        public string LegacyBridgeIpAddress { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyBridgeIpAddress() => false;

        [XmlElement("Username")]
        public string LegacyUsername { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyUsername() => false;

        [XmlElement("BridgeId")]
        public string LegacyBridgeId { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyBridgeId() => false;

        // Backward compat: absorb removed property from old configs
        public bool UseLightGroups { get; set; } = true;
        public bool ShouldSerializeUseLightGroups() => false;

        /// <summary>
        /// Migrates legacy single-bridge config (BridgeIpAddress/Username/BridgeId)
        /// into the new Bridges list. Returns true if migration occurred.
        /// </summary>
        public bool MigrateLegacyConfig()
        {
            if (Bridges.Count > 0 || string.IsNullOrWhiteSpace(LegacyBridgeIpAddress))
            {
                return false;
            }

            var bridge = new HueBridge
            {
                IpAddress = LegacyBridgeIpAddress,
                Username = LegacyUsername,
                Name = !string.IsNullOrWhiteSpace(LegacyBridgeId) ? LegacyBridgeId : "Bridge"
            };
            Bridges.Add(bridge);

            // Assign this bridge to all existing profiles that lack a BridgeId
            foreach (var profile in Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.BridgeId))
                {
                    profile.BridgeId = bridge.Id;
                }
            }

            // Clear legacy fields
            LegacyBridgeIpAddress = string.Empty;
            LegacyUsername = string.Empty;
            LegacyBridgeId = string.Empty;

            return true;
        }
    }
}
