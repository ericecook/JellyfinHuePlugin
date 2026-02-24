using System.ComponentModel.DataAnnotations;

namespace JellyfinHuePlugin.Api
{
    public class AuthenticationRequest
    {
        [Required]
        public string BridgeIp { get; set; } = string.Empty;
    }

    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public string? Username { get; set; }
        public string? Error { get; set; }
    }

    public class TestLightRequest
    {
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
