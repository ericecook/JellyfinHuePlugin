using System.Collections.Generic;

namespace JellyfinHuePlugin.Services
{
    public class HueBridgeDiscovery
    {
        public string Id { get; set; } = string.Empty;
        public string InternalIpAddress { get; set; } = string.Empty;
    }

    public class HueLight
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public HueLightState? State { get; set; }
    }

    public class HueGroup
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public List<string> Lights { get; set; } = new();
        public HueLightState? Action { get; set; }
    }

    public class HueScene
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public List<string> Lights { get; set; } = new();
    }

    public class HueLightState
    {
        public bool? On { get; set; }
        public int? Bri { get; set; } // 0-254
        public int? Hue { get; set; } // 0-65535
        public int? Sat { get; set; } // 0-254
        public int? Ct { get; set; }  // Color temperature
        public List<float>? Xy { get; set; } // CIE color space
        public int? TransitionTime { get; set; } // In 100ms increments
    }
}
