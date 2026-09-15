using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using JellyfinHuePlugin.Configuration;
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
        /// <summary>The configuration entry's GUID (HueBridge.Id), which the page uses to link profiles.</summary>
        public string? Id { get; set; }
        /// <summary>The bridge's own 16-hex id (HueBridge.HardwareId), the TLS certificate subject.</summary>
        public string? HardwareId { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// GET api/hueplugin/targets. Groups holds the bridge's rooms and zones keyed by grouped_light
    /// id, the value a profile stores as TargetGroupId; Scenes holds its scenes keyed by scene id.
    /// </summary>
    public class TargetsResult
    {
        public Dictionary<string, HueGroupResource> Groups { get; set; } = new();
        public Dictionary<string, HueSceneResource> Scenes { get; set; } = new();
    }

    /// <summary>
    /// POST api/hueplugin/test. The profile is the page's in-memory copy, saved or not; the
    /// bridge is looked up server-side from its BridgeId.
    /// </summary>
    public class TestLightRequest
    {
        [Required]
        public LightAction? Action { get; set; }

        /// <summary>The whole profile as the page holds it; unsaved editor values are allowed.</summary>
        [Required]
        public LightControlProfile? Profile { get; set; }
    }

    /// <summary>What the page shows under a Test button. Error is null on success.</summary>
    public class TestLightResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
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
        /// <summary>Filled whenever the bridge answered /api/0/config, even when Success is false. Learned over the chain-only client before any pinning, so these facts are trusted to chain level only; Success is the pinned verdict.</summary>
        public string? HardwareId { get; set; }
        public string? ModelId { get; set; }
        public string? SoftwareVersion { get; set; }
        public string? ApiVersion { get; set; }
        public bool SupportsV2 { get; set; }
    }
}
