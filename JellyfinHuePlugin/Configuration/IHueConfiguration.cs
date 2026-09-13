namespace JellyfinHuePlugin.Configuration
{
    /// <summary>
    /// The plugin's live configuration and its save, without the static plugin instance.
    /// Services and the API controller depend on this; the <see cref="Plugin"/> supplies it.
    /// </summary>
    public interface IHueConfiguration
    {
        PluginConfiguration Current { get; }

        void Save();
    }
}
