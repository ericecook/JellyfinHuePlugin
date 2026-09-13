using System;
using System.Collections.Generic;
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
        /// <summary>The configuration entry's GUID (HueBridge.Id), which the page uses to link profiles.</summary>
        public string? Id { get; set; }
        /// <summary>The bridge's own 16-hex id (HueBridge.HardwareId), the TLS certificate subject.</summary>
        public string? HardwareId { get; set; }
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
        /// <summary>Turn the group off instead of setting a brightness, as a turn-off play state does.</summary>
        public bool TurnOff { get; set; }
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
        /// <summary>Filled whenever the bridge answered /api/0/config, even when Success is false. Learned over the chain-only client before any pinning, so these facts are trusted to chain level only; Success is the pinned verdict.</summary>
        public string? HardwareId { get; set; }
        public string? ModelId { get; set; }
        public string? SoftwareVersion { get; set; }
        public string? ApiVersion { get; set; }
        public bool SupportsV2 { get; set; }
    }

    public static class MigrationStatus
    {
        public const string Ok = "ok";
        public const string Unauthenticated = "unauthenticated";
        public const string Unreachable = "unreachable";
        public const string Skipped = "skipped";
    }

    /// <summary>What POST api/hueplugin/migrate did. The page turns it into a banner.</summary>
    public class MigrationReport
    {
        /// <summary>True when the configuration was modified and must be saved.</summary>
        public bool Changed { get; set; }
        public List<BridgeMigration> Bridges { get; set; } = new();
        public List<ProfileMigration> Profiles { get; set; } = new();
    }

    public class BridgeMigration
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>One of MigrationStatus.Ok, Unauthenticated, Unreachable.</summary>
        public string Status { get; set; } = MigrationStatus.Ok;
        public bool HardwareIdLearned { get; set; }
    }

    public class ProfileMigration
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>MigrationStatus.Ok when its bridge was checked, Skipped otherwise.</summary>
        public string Status { get; set; } = MigrationStatus.Ok;
        /// <summary>Field names rewritten to v2 ids: TargetGroupId, PlaySceneId, PauseSceneId, StopSceneId.</summary>
        public List<string> Rewritten { get; set; } = new();
        /// <summary>Field names whose stored id matched nothing on the bridge; the stored value was left alone.</summary>
        public List<string> Unresolved { get; set; } = new();
    }
}
