using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin
{
    /// <summary>
    /// Jellyfin's handle on the plugin: identity, the configuration page, the configuration file,
    /// and the two one-time conversions of older configurations. Every service is created by the
    /// container (see <see cref="PluginServiceRegistrator"/>); this class only binds the
    /// configuration store to itself and reacts to configuration saves made from the page.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;
        private readonly ILogger<Plugin> _logger;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            HueConfigurationStore store,
            HueService hueService,
            HueResourceCatalog catalog,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _hueService = hueService;
            _catalog = catalog;
            _logger = logger;

            // Both conversions below iterate the lists, and an older plugin page could store a null profile
            var repaired = Configuration.RemoveInvalidEntries();
            if (repaired > 0)
            {
                _logger.LogWarning("Repaired {Count} invalid entries in the stored configuration", repaired);
                SaveConfiguration();
            }

            // Migrate legacy single-bridge config to new Bridges list
            if (Configuration.MigrateLegacyConfig())
            {
                _logger.LogInformation("Migrated legacy single-bridge config to Bridges list");
                SaveConfiguration();
            }

            // One-time conversion of brightness from 0-254 to percent (schema version 2)
            if (Configuration.MigrateBrightnessToPercent())
            {
                _logger.LogInformation("Converted profile brightness to percent (schema version 2)");
                SaveConfiguration();
            }

            store.Attach(this);
            _logger.LogInformation("Jellyfin Hue Plugin initialized successfully");
        }

        public override string Name => "Hue Lighting Control";

        public override Guid Id => Guid.Parse("2a5f5b3e-8c9d-4f1a-9b7e-6d3c4e5f6a7b");

        public override string Description => "Control Philips Hue lights based on Jellyfin playback events";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Hue Lighting Control",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }

        /// <summary>
        /// The web client's generic configuration save lands here (the plugin's own API writes
        /// through <see cref="BasePlugin{T}.SaveConfiguration()"/> and never does). Null bridges
        /// and profiles, which a plugin page before 4.0 could post, are dropped first. A bridge whose
        /// address or application key changed on the page still carries the previous bridge's
        /// pinned id and cached rooms, so the pin is cleared before the single write and the
        /// caches dropped after it. <see cref="HueBridge.HardwareId"/> is server-owned on this
        /// path: the page holds a copy of the bridge list loaded once and posts the whole list
        /// back, so a pin the server learned since (a re-authentication) would be overwritten by
        /// the page's stale one - every untouched bridge therefore keeps the live value. A
        /// failure in the change detection is logged and never blocks the save.
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var incoming = (PluginConfiguration)configuration;
            var dropped = incoming.RemoveInvalidEntries();
            if (dropped > 0)
            {
                _logger.LogWarning("Dropped {Count} invalid entries from a configuration save", dropped);
            }

            var changes = BridgeChanges.Result.None;
            try
            {
                changes = BridgeChanges.Between(Configuration.Bridges, incoming.Bridges);
                foreach (var (_, updated) in changes.Changed)
                {
                    _logger.LogInformation("Bridge {BridgeName} changed address or application key on the plugin page; clearing its pinned bridge id and cached resources", updated.Name);
                    updated.HardwareId = string.Empty;
                }

                foreach (var (old, updated) in changes.Unchanged)
                {
                    updated.HardwareId = old.HardwareId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bridge change detection failed; configuration saved without cache invalidation");
                changes = BridgeChanges.Result.None;
            }

            base.UpdateConfiguration(incoming);

            foreach (var (old, updated) in changes.Changed)
            {
                _catalog.Invalidate(old);
                // Both hosts: the old address keeps a learned id that is no longer this bridge's,
                // and the new one may already carry an id learned for whatever answered there.
                _hueService.ForgetHost(old.IpAddress);
                _hueService.ForgetHost(updated.IpAddress);
            }

            foreach (var old in changes.Removed)
            {
                _catalog.Invalidate(old);
                _hueService.ForgetHost(old.IpAddress);
            }
        }
    }
}
