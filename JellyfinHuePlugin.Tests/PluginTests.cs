using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests
{
    /// <summary>
    /// A real Plugin over a temporary configuration directory. The XML serializer is a mock so the
    /// tests control what "on disk" holds and can count writes.
    /// </summary>
    public class PluginTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hue-plugin-tests-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IApplicationPaths> _paths = new();
        private readonly Mock<IXmlSerializer> _xml = new();
        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly HueConfigurationStore _store = new();
        private readonly List<object> _written = new();

        public PluginTests()
        {
            Directory.CreateDirectory(_dir);
            _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_dir);
            // BasePlugin<T>'s constructor also builds the plugin's data folder path from PluginsPath.
            _paths.SetupGet(p => p.PluginsPath).Returns(_dir);
            _xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
                .Callback<object, string>((o, _) => _written.Add(o));
            _hue = new Mock<HueService>(new NullLogger<HueService>(), new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance)) { CallBase = false };
            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger<HueResourceCatalog>.Instance) { CallBase = false };
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private static HueBridge Bridge(string id = "b1", string ip = "192.168.1.50", string key = "key", string hardwareId = "001788fffe123456") =>
            new() { Id = id, Name = "Bridge", IpAddress = ip, Username = key, HardwareId = hardwareId };

        private Plugin Load(PluginConfiguration onDisk)
        {
            // BasePlugin asks the serializer on first Configuration access whether or not the file exists.
            _xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(onDisk);
            return new Plugin(_paths.Object, _xml.Object, _store, _hue.Object, _catalog.Object, NullLogger<Plugin>.Instance);
        }

        private static PluginConfiguration Config(params HueBridge[] bridges) =>
            new() { SchemaVersion = PluginConfiguration.CurrentSchemaVersion, EnablePlugin = true, Bridges = new List<HueBridge>(bridges) };

        [Fact]
        public void Constructor_AttachesTheStore()
        {
            var plugin = Load(Config(Bridge()));

            _store.Current.Should().BeSameAs(plugin.Configuration);
            _store.Save();
            _written.Should().ContainSingle().Which.Should().BeSameAs(plugin.Configuration);
        }

        [Fact]
        public void Constructor_RunsTheBrightnessConversionOnce()
        {
            var onDisk = new PluginConfiguration
            {
                SchemaVersion = 0,
                Bridges = new List<HueBridge> { Bridge() },
                Profiles = new List<LightControlProfile> { new() { Name = "p", PlayBrightness = 254, PauseBrightness = 127, StopBrightness = 0 } }
            };

            var plugin = Load(onDisk);

            plugin.Configuration.SchemaVersion.Should().Be(PluginConfiguration.CurrentSchemaVersion);
            plugin.Configuration.Profiles[0].PlayBrightness.Should().Be(100);
            _written.Should().ContainSingle();
        }

        [Fact]
        public void UpdateConfiguration_AddressChanged_ClearsThePinBeforeTheWriteAndInvalidatesAfter()
        {
            var old = Bridge();
            var plugin = Load(Config(old));
            var incoming = Config(Bridge(ip: "192.168.1.60"));

            plugin.UpdateConfiguration(incoming);

            _written.Should().ContainSingle().Which.Should().BeSameAs(incoming);
            incoming.Bridges[0].HardwareId.Should().BeEmpty();
            plugin.Configuration.Should().BeSameAs(incoming);
            _catalog.Verify(c => c.Invalidate(old), Times.Once);
            _hue.Verify(h => h.ForgetHost("192.168.1.50"), Times.Once);
        }

        [Fact]
        public void UpdateConfiguration_KeyChanged_ClearsThePin()
        {
            var plugin = Load(Config(Bridge()));
            var incoming = Config(Bridge(key: "newkey"));

            plugin.UpdateConfiguration(incoming);

            incoming.Bridges[0].HardwareId.Should().BeEmpty();
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Once);
        }

        [Fact]
        public void UpdateConfiguration_BridgeRemoved_InvalidatesAndForgetsIt()
        {
            var old = Bridge();
            var plugin = Load(Config(old));

            plugin.UpdateConfiguration(Config());

            _catalog.Verify(c => c.Invalidate(old), Times.Once);
            _hue.Verify(h => h.ForgetHost("192.168.1.50"), Times.Once);
            _written.Should().ContainSingle();
        }

        [Fact]
        public void UpdateConfiguration_RenameOnly_KeepsThePinAndTouchesNoCache()
        {
            var plugin = Load(Config(Bridge()));
            var incoming = Config(Bridge());
            incoming.Bridges[0].Name = "Living room";

            plugin.UpdateConfiguration(incoming);

            incoming.Bridges[0].HardwareId.Should().Be("001788fffe123456");
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Never);
            _hue.Verify(h => h.ForgetHost(It.IsAny<string>()), Times.Never);
            _written.Should().ContainSingle();
        }

        [Fact]
        public void UpdateConfiguration_NeverLogsTheKey()
        {
            var log = new CapturingLogger();
            _xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(Config(Bridge(key: "SECRET-OLD")));
            var plugin = new Plugin(_paths.Object, _xml.Object, _store, _hue.Object, _catalog.Object, log);

            plugin.UpdateConfiguration(Config(Bridge(key: "SECRET-NEW")));

            log.Lines.Should().NotContain(l => l.Contains("SECRET"));
            log.Lines.Should().Contain(l => l.Contains("changed address or application key on the plugin page"));
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<Plugin>
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }
    }
}
