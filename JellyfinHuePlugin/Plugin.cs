using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
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
        private PlaybackSessionManager? _playbackManager;
        private bool _disposed = false;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ISessionManager sessionManager,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            _sessionManager = sessionManager;
            _logger = loggerFactory.CreateLogger<Plugin>();
            _hueService = new HueService(loggerFactory.CreateLogger<HueService>());
            
            Instance = this;
            
            // Initialize playback manager
            InitializePlaybackManager(loggerFactory);
        }

        private void InitializePlaybackManager(ILoggerFactory loggerFactory)
        {
            try
            {
                _playbackManager = new PlaybackSessionManager(
                    _sessionManager,
                    loggerFactory.CreateLogger<PlaybackSessionManager>(),
                    _hueService,
                    () => Configuration);
                    
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
