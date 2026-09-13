using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace JellyfinHuePlugin.Configuration
{
    public class HueBridge
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        /// <summary>The bridge's own id (16 hex characters, lower case), the subject of its TLS certificate. Empty until learned.</summary>
        public string BridgeId { get; set; } = string.Empty;
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

        // Brightness, percent (0–100). Configurations written before schema version 2 used 0–254
        // and are converted once by PluginConfiguration.MigrateBrightnessToPercent().
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

        // Backward compat: old configs had a single TransitionDuration property.
        // When deserialized from XML, apply it to all three per-state durations.
        // ShouldSerialize* only applies to XmlSerializer; the config page talks JSON
        // (System.Text.Json), which would otherwise emit this as 0 and call the setter
        // on the way back, clobbering the real per-state values. Hence [JsonIgnore].
        [XmlElement("TransitionDuration")]
        [JsonIgnore]
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

        /// <summary>
        /// Version of the stored shape. 0 is everything written before percent brightness; a
        /// deserialised file without the element reads as 0, which is what triggers the
        /// one-time conversion. Fresh configurations also start at 0 and are stamped on first load.
        /// </summary>
        public int SchemaVersion { get; set; } = 0;

        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// Converts every profile's brightness from the v1 0–254 scale to percent, once, and
        /// stamps the schema version. Returns true when something was written, including the
        /// first load of a fresh configuration.
        /// </summary>
        public bool MigrateBrightnessToPercent()
        {
            if (SchemaVersion >= CurrentSchemaVersion)
            {
                return false;
            }

            foreach (var profile in Profiles)
            {
                profile.PlayBrightness = ToPercent(profile.PlayBrightness);
                profile.PauseBrightness = ToPercent(profile.PauseBrightness);
                profile.StopBrightness = ToPercent(profile.StopBrightness);
            }

            SchemaVersion = CurrentSchemaVersion;
            return true;
        }

        internal static int ToPercent(int v1Brightness)
            => Math.Clamp((int)Math.Round(v1Brightness * 100.0 / 254.0, MidpointRounding.AwayFromZero), 0, 100);

        // Legacy single-bridge fields — absorbed on XML deserialization, never written
        // back and never exposed over the JSON config API.
        // MigrateLegacyConfig() converts them to a Bridges entry.
        [XmlElement("BridgeIpAddress")]
        [JsonIgnore]
        public string LegacyBridgeIpAddress { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyBridgeIpAddress() => false;

        [XmlElement("Username")]
        [JsonIgnore]
        public string LegacyUsername { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyUsername() => false;

        [XmlElement("BridgeId")]
        [JsonIgnore]
        public string LegacyBridgeId { get; set; } = string.Empty;
        public bool ShouldSerializeLegacyBridgeId() => false;

        // Backward compat: absorb removed property from old configs
        [JsonIgnore]
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
