using JellyfinHuePlugin.Configuration;

namespace JellyfinHuePlugin.Tests.Support
{
    /// <summary>In-memory <see cref="IHueConfiguration"/>: hands out one object and counts saves.</summary>
    public sealed class FakeHueConfiguration : IHueConfiguration
    {
        public FakeHueConfiguration(PluginConfiguration configuration)
        {
            Current = configuration;
        }

        public PluginConfiguration Current { get; }

        public int SaveCount { get; private set; }

        public void Save() => SaveCount++;
    }
}
