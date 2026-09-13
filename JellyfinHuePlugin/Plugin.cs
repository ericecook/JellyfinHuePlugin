using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using JellyfinHuePlugin.Managers;

namespace JellyfinHuePlugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<Plugin> _logger;
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;
        private readonly ConfigurationMigrator _migrator;
        private readonly IHueConfiguration _configuration;
        private readonly IMediaSegmentManager _segmentManager;
        private readonly ILibraryManager _libraryManager;
        private PlaybackSessionManager? _playbackManager;
        private bool _disposed = false;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ISessionManager sessionManager,
            IMediaSegmentManager segmentManager,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            _sessionManager = sessionManager;
            _segmentManager = segmentManager;
            _libraryManager = libraryManager;
            _logger = loggerFactory.CreateLogger<Plugin>();
            var mdns = new MdnsBridgeDiscovery(loggerFactory.CreateLogger<MdnsBridgeDiscovery>());
            _hueService = new HueService(loggerFactory.CreateLogger<HueService>(), mdns);
            _catalog = new HueResourceCatalog(_hueService, loggerFactory.CreateLogger<HueResourceCatalog>());
            _migrator = new ConfigurationMigrator(_hueService, _catalog, loggerFactory.CreateLogger<ConfigurationMigrator>());

            Instance = this;

            var configurationStore = new HueConfigurationStore();
            configurationStore.Attach(this);
            _configuration = configurationStore;

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

            // Initialize playback manager
            InitializePlaybackManager(loggerFactory);
        }

        private void InitializePlaybackManager(ILoggerFactory loggerFactory)
        {
            try
            {
                var executor = new LightCommandExecutor(_hueService, _catalog, loggerFactory.CreateLogger<LightCommandExecutor>());
                _playbackManager = new PlaybackSessionManager(
                    _sessionManager,
                    loggerFactory.CreateLogger<PlaybackSessionManager>(),
                    executor,
                    _configuration,
                    _segmentManager,
                    _libraryManager,
                    TimeProvider.System);
                _playbackManager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

                _logger.LogInformation("Jellyfin Hue Plugin initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize playback manager - plugin will load but may not function");
                // Don't throw - allow plugin to load even if session manager fails
            }
        }

        public override string Name => "Hue Lighting Control";

        public override Guid Id => Guid.Parse("2a5f5b3e-8c9d-4f1a-9b7e-6d3c4e5f6a7b");

        public override string Description => "Control Philips Hue lights based on Jellyfin playback events";

        public static Plugin? Instance { get; private set; }

        public HueService HueService => _hueService;

        public HueResourceCatalog Catalog => _catalog;

        public ConfigurationMigrator Migrator => _migrator;

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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _playbackManager?.Dispose();
                _hueService.Dispose();
            }

            _disposed = true;
        }
    }
}
