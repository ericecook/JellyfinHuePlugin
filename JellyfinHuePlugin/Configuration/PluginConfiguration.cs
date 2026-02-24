using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

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
        public bool UseLightGroups { get; set; } = true;
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
        
        // Transition settings
        public int TransitionDuration { get; set; } = 4; // In deciseconds (0.1s increments), default 0.4s
        
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
    }
}