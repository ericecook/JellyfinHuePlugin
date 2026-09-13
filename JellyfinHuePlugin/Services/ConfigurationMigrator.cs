using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// One-time upgrade of a stored configuration to CLIP v2: learns each bridge's HardwareId
    /// and rewrites every profile's v1 group and scene ids to v2 UUIDs through the catalog.
    /// Runs on every plugin-page load and is cheap once nothing is left to do. Mutates the
    /// configuration in place and reports what changed; the caller decides whether to save.
    /// Never writes configuration itself and never logs an application key.
    /// </summary>
    public class ConfigurationMigrator
    {
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;
        private readonly ILogger _logger;

        public ConfigurationMigrator(HueService hueService, HueResourceCatalog catalog, ILogger logger)
        {
            _hueService = hueService;
            _catalog = catalog;
            _logger = logger;
        }

        public virtual async Task<MigrationReport> MigrateAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            var report = new MigrationReport();

            // A page load is the one moment the user is looking: show them the bridge as it is now,
            // not as it was when the cache was filled.
            _catalog.InvalidateAll();

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bridge in config.Bridges)
            {
                var entry = new BridgeMigration { Id = bridge.Id, Name = bridge.Name };
                report.Bridges.Add(entry);

                if (string.IsNullOrWhiteSpace(bridge.Username) || string.IsNullOrWhiteSpace(bridge.IpAddress))
                {
                    entry.Status = MigrationStatus.Unauthenticated;
                    continue;
                }

                try
                {
                    if (string.IsNullOrWhiteSpace(bridge.HardwareId))
                    {
                        var info = await _hueService.GetBridgeInfoAsync(bridge.IpAddress, cancellationToken);
                        if (info != null)
                        {
                            bridge.HardwareId = info.HardwareId;
                            entry.HardwareIdLearned = true;
                            report.Changed = true;
                            _logger.LogInformation("Learned bridge id {HardwareId} for bridge {BridgeName}", info.HardwareId, bridge.Name);
                        }
                    }

                    if (await _catalog.GetGroupsAsync(bridge, cancellationToken) == null)
                    {
                        entry.Status = MigrationStatus.Unreachable;
                        _logger.LogWarning("Bridge {BridgeName} could not be reached during migration; its profiles were not checked", bridge.Name);
                        continue;
                    }

                    entry.Status = MigrationStatus.Ok;
                    reachable.Add(bridge.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    entry.Status = MigrationStatus.Unreachable;
                    _logger.LogWarning(ex, "Bridge {BridgeName} could not be reached during migration; its profiles were not checked", bridge.Name);
                }
            }

            foreach (var profile in config.Profiles)
            {
                var entry = new ProfileMigration { Id = profile.Id, Name = profile.Name };
                report.Profiles.Add(entry);

                var bridge = FindBridge(config, profile);
                if (bridge == null || !reachable.Contains(bridge.Id))
                {
                    entry.Status = MigrationStatus.Skipped;
                    continue;
                }

                entry.Status = MigrationStatus.Ok;

                if (IsLegacyId(profile.TargetGroupId) && profile.TargetGroupId != "0")
                {
                    var resolved = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
                    Apply(report, entry, profile, "TargetGroupId", profile.TargetGroupId, resolved, v => profile.TargetGroupId = v);
                }

                if (IsLegacyId(profile.PlaySceneId))
                {
                    var resolved = await _catalog.ResolveSceneAsync(bridge, profile.PlaySceneId, cancellationToken);
                    Apply(report, entry, profile, "PlaySceneId", profile.PlaySceneId, resolved, v => profile.PlaySceneId = v);
                }

                if (IsLegacyId(profile.PauseSceneId))
                {
                    var resolved = await _catalog.ResolveSceneAsync(bridge, profile.PauseSceneId, cancellationToken);
                    Apply(report, entry, profile, "PauseSceneId", profile.PauseSceneId, resolved, v => profile.PauseSceneId = v);
                }

                if (IsLegacyId(profile.StopSceneId))
                {
                    var resolved = await _catalog.ResolveSceneAsync(bridge, profile.StopSceneId, cancellationToken);
                    Apply(report, entry, profile, "StopSceneId", profile.StopSceneId, resolved, v => profile.StopSceneId = v);
                }
            }

            return report;
        }

        /// <summary>Same rule as PlaybackSessionManager: an explicit BridgeId must match; an empty one means the only bridge, if there is exactly one.</summary>
        private static HueBridge? FindBridge(PluginConfiguration config, LightControlProfile profile) =>
            string.IsNullOrWhiteSpace(profile.BridgeId)
                ? (config.Bridges.Count == 1 ? config.Bridges[0] : null)
                : config.Bridges.FirstOrDefault(b => b.Id == profile.BridgeId);

        /// <summary>Non-empty and not a v2 UUID: a v1 group number or v1 scene id.</summary>
        private static bool IsLegacyId(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !Guid.TryParseExact(value, "D", out _);

        private void Apply(MigrationReport report, ProfileMigration entry, LightControlProfile profile, string field, string oldValue, string? resolved, Action<string> assign)
        {
            if (resolved == null)
            {
                // The catalog already warned with the re-select advice; the page banner repeats it.
                entry.Unresolved.Add(field);
                return;
            }

            assign(resolved);
            entry.Rewritten.Add(field);
            report.Changed = true;
            _logger.LogInformation("Rewrote {Field} of profile {ProfileName} from {OldValue} to {NewValue}", field, profile.Name, oldValue, resolved);
        }
    }
}
