using System;
using System.ComponentModel.DataAnnotations;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Api
{
    /// <summary>Accepts an IP address or host name with an optional port, as BridgeUri.TryParseHost defines it.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class BridgeAddressAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => value is string text && BridgeUri.TryParseHost(text, out _);

        public override string FormatErrorMessage(string name) => $"{name} must be an IP address or host name, optionally with a port.";
    }

    /// <summary>Accepts a Hue application key: letters, digits, dot, dash and underscore only.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class BridgeKeyAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => value is string text && BridgeUri.IsValidSegment(text);

        public override string FormatErrorMessage(string name) => $"{name} is not a valid Hue application key.";
    }

    public class AuthenticationRequest
    {
        [Required]
        [BridgeAddress]
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
        [BridgeAddress]
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
        [BridgeAddress]
        public string BridgeIp { get; set; } = string.Empty;
    }

    public class VerifyConnectionRequest
    {
        [Required]
        [BridgeAddress]
        public string BridgeIp { get; set; } = string.Empty;
        [Required]
        [BridgeKey]
        public string Username { get; set; } = string.Empty;
    }

    public class VerifyConnectionResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
