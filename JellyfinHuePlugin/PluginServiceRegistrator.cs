using System;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin
{
    /// <summary>
    /// Composition root. Jellyfin calls this while building its service collection, before any
    /// plugin instance exists; the <see cref="Plugin"/> is later created through the same
    /// container and receives the store, service and catalog registered here. The container owns
    /// disposal, and the host starts and stops the playback listener.
    /// Nothing registered here may throw in its constructor: a throwing singleton fails
    /// Jellyfin's host start, not just this plugin.
    /// </summary>
    public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.TryAddSingleton<TimeProvider>(TimeProvider.System);
            serviceCollection.AddSingleton<MdnsBridgeDiscovery>();
            // Explicit factory: HueService has a second (test-seam) constructor and the container
            // must never be left to choose between them.
            serviceCollection.AddSingleton(sp => new HueService(
                sp.GetRequiredService<ILogger<HueService>>(),
                sp.GetRequiredService<MdnsBridgeDiscovery>()));
            serviceCollection.AddSingleton<HueResourceCatalog>();
            serviceCollection.AddSingleton<ConfigurationMigrator>();
            serviceCollection.AddSingleton<LightCommandExecutor>();
            serviceCollection.AddSingleton<HueConfigurationStore>();
            serviceCollection.AddSingleton<IHueConfiguration>(sp => sp.GetRequiredService<HueConfigurationStore>());
            serviceCollection.AddSingleton<PlaybackSessionManager>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlaybackSessionManager>());
        }
    }
}
