using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    // Sent to the bridge with HueService.HueJsonOptions (camelCase, nulls omitted).
    // The Hue v1 API keys are all lowercase, so multi-word names need an explicit
    // [JsonPropertyName]; camelCase "transitionTime" is rejected as an unknown parameter.
    public class HueLightState
    {
        public bool? On { get; set; }
        public int? Bri { get; set; } // 0-254
        public int? Hue { get; set; } // 0-65535
        public int? Sat { get; set; } // 0-254
        public int? Ct { get; set; }  // Color temperature
        public List<float>? Xy { get; set; } // CIE color space
        [JsonPropertyName("transitiontime")]
        public int? TransitionTime { get; set; } // In 100ms increments
    }

    /// <summary>Unauthenticated bridge facts from GET /api/0/config.</summary>
    public sealed record HueBridgeInfo(string BridgeId, string SoftwareVersion, string ApiVersion, string ModelId)
    {
        /// <summary>CLIP v2 needs bridge software 1948086000 or newer; the round v1 bridge never qualifies.</summary>
        public bool SupportsV2 => long.TryParse(SoftwareVersion, out var version) && version >= HueService.MinimumV2SoftwareVersion;
    }

    public sealed record AuthenticationOutcome(string Username, string BridgeId);

    /// <summary>A room, zone or the bridge home, with the grouped_light service that controls it.</summary>
    public sealed record HueGroupResource(string Id, string GroupedLightId, string Name, string Type, string? IdV1);

    /// <summary>A scene. GroupId is the room or zone it belongs to; the catalog fills GroupName and GroupedLightId.</summary>
    public sealed record HueSceneResource(string Id, string Name, string GroupId, string? IdV1)
    {
        public string GroupName { get; init; } = string.Empty;
        public string GroupedLightId { get; init; } = string.Empty;

        /// <summary>The config page reads scenes[id].Group for its dropdown label.</summary>
        public string Group => GroupName;
    }

    public sealed record HueLightResource(string Id, string Name, bool On, double? Brightness, string? IdV1);

    /// <summary>Body of PUT /clip/v2/resource/grouped_light/{id}: only the set fields are sent.</summary>
    public sealed class GroupedLightState
    {
        public bool? On { get; set; }
        /// <summary>Percent, 0–100. The bridge treats 0 as its lowest brightness.</summary>
        public double? Brightness { get; set; }
        /// <summary>Transition in milliseconds.</summary>
        public int? DurationMs { get; set; }

        internal Dictionary<string, object> ToRequestBody()
        {
            var body = new Dictionary<string, object>();
            if (On.HasValue)
            {
                body["on"] = new { on = On.Value };
            }

            if (Brightness.HasValue)
            {
                body["dimming"] = new { brightness = Brightness.Value };
            }

            if (DurationMs.HasValue)
            {
                body["dynamics"] = new { duration = DurationMs.Value };
            }

            return body;
        }
    }
}
