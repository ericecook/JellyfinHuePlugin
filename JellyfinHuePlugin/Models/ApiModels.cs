using System.ComponentModel.DataAnnotations;

namespace JellyfinHuePlugin.Api
{
    public class AuthenticationRequest
    {
        [Required]
        public string BridgeIp { get; set; } = string.Empty;
        public string? BridgeId { get; set; }
        public string? BridgeName { get; set; }
    }

    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public string? Username { get; set; }
        public string? BridgeId { get; set; }
        public string? Error { get; set; }
    }

    public class AddBridgeRequest
    {
        [Required]
        public string IpAddress { get; set; } = string.Empty;
        public string Name { get; set; } = "Bridge";
    }

    public class BridgeInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; }
    }

    public class TestLightRequest
    {
        public string? BridgeId { get; set; }
        public string? GroupId { get; set; }
        public string? SceneId { get; set; }
        public int Brightness { get; set; } = 100;
    }

    public class TestConnectionRequest
    {
        [Required]
        public string BridgeIp { get; set; } = string.Empty;
    }
}
