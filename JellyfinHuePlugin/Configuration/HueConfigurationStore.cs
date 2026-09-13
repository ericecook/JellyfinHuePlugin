using System;

namespace JellyfinHuePlugin.Configuration
{
    /// <summary>
    /// <see cref="IHueConfiguration"/> bound to the <see cref="Plugin"/> Jellyfin creates. It is
    /// registered before the plugin exists and attached from the plugin's constructor; Jellyfin
    /// creates plugins before it starts hosted services or serves requests, so an unattached
    /// use is a wiring bug and throws rather than pretending there is no configuration.
    /// </summary>
    public sealed class HueConfigurationStore : IHueConfiguration
    {
        private Plugin? _plugin;

        internal void Attach(Plugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        public PluginConfiguration Current => Attached.Configuration;

        public void Save() => Attached.SaveConfiguration();

        private Plugin Attached => _plugin ?? throw new InvalidOperationException("The Hue plugin has not been loaded yet");
    }
}
