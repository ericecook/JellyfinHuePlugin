using MediaBrowser.Model.Plugins;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace JellyfinHuePlugin.Configuration
{
    public class LightControlProfile
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "Default Profile";

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

        // Outro detection
        public bool EnableOutroLights { get; set; } = false;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string BridgeIpAddress { get; set; } = string.Empty;
        public string BridgeId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        public List<LightControlProfile> Profiles { get; set; } = new List<LightControlProfile>();

        public bool EnablePlugin { get; set; } = true;

        // Backward compat: absorb removed property from old configs
        public bool UseLightGroups { get; set; } = true;
        public bool ShouldSerializeUseLightGroups() => false;
    }
}