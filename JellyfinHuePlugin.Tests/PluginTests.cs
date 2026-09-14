using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>HardwareId of every bridge as it stood at each write - the object is mutated after.</summary>
        private readonly List<string[]> _pinsAtWrite = new();
        /// <summary>Catalog invocation count at each write, to place Invalidate after the save.</summary>
        private readonly List<int> _catalogCallsAtWrite = new();

        public PluginTests()
        {
            Directory.CreateDirectory(_dir);
            _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_dir);
            // BasePlugin<T>'s constructor also builds the plugin's data folder path from PluginsPath.
            _paths.SetupGet(p => p.PluginsPath).Returns(_dir);
            _hue = new Mock<HueService>(new NullLogger<HueService>(), new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance)) { CallBase = false };
            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger<HueResourceCatalog>.Instance) { CallBase = false };
            // Snapshot inside the callback: the configuration object keeps being mutated afterwards,
            // so what it holds when the test asserts is not what was written.
            _xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
                .Callback<object, string>((o, _) =>
                {
                    _written.Add(o);
                    _pinsAtWrite.Add(o is PluginConfiguration c
                        ? c.Bridges.Select(b => b.HardwareId).ToArray()
                        : Array.Empty<string>());
                    _catalogCallsAtWrite.Add(_catalog.Invocations.Count);
                });
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
            _pinsAtWrite.Should().ContainSingle().Which.Should().Equal(string.Empty);
            _catalogCallsAtWrite.Should().ContainSingle().Which.Should().Be(0, "the caches are dropped only once the save succeeded");
            plugin.Configuration.Should().BeSameAs(incoming);
            _catalog.Verify(c => c.Invalidate(old), Times.Once);
            // Both the address it left and the one it moved to: either may hold a learned id.
            _hue.Verify(h => h.ForgetHost("192.168.1.50"), Times.Once);
            _hue.Verify(h => h.ForgetHost("192.168.1.60"), Times.Once);
        }

        [Theory]
        [InlineData("")]
        [InlineData("001788fffeaaaaaa")]
        public void UpdateConfiguration_UnchangedBridge_KeepsTheLivePinWhateverThePagePosted(string postedPin)
        {
            // The page holds a copy of the bridge list loaded once and posts it back whole, so a
            // pin the server learned since (a re-authentication) must not be overwritten by it.
            var plugin = Load(Config(Bridge(hardwareId: "001788fffe123456")));
            var incoming = Config(Bridge(hardwareId: postedPin));

            plugin.UpdateConfiguration(incoming);

            _pinsAtWrite.Should().ContainSingle().Which.Should().Equal("001788fffe123456");
            incoming.Bridges[0].HardwareId.Should().Be("001788fffe123456");
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Never);
            _hue.Verify(h => h.ForgetHost(It.IsAny<string>()), Times.Never);
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

        [Fact]
        public void Constructor_LogsTheRepairedCount()
        {
            var log = new CapturingLogger();
            var onDisk = Config(Bridge());
            onDisk.Profiles = new List<LightControlProfile> { new() { Name = "kept" }, null! };
            _xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(onDisk);

            _ = new Plugin(_paths.Object, _xml.Object, _store, _hue.Object, _catalog.Object, log);

            log.Lines.Should().Contain(l => l.Contains("Repaired 1 invalid entries in the stored configuration"));
        }

        [Fact]
        public void UpdateConfiguration_LogsTheDroppedCount()
        {
            var log = new CapturingLogger();
            _xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(Config(Bridge()));
            var plugin = new Plugin(_paths.Object, _xml.Object, _store, _hue.Object, _catalog.Object, log);
            var incoming = Config(null!, Bridge());
            incoming.Profiles = new List<LightControlProfile> { null!, new() { Name = "p" } };

            plugin.UpdateConfiguration(incoming);

            log.Lines.Should().Contain(l => l.Contains("Dropped 2 invalid entries from a configuration save"));
        }

        [Fact]
        public void Constructor_RemovesNullEntriesFromTheStoredConfigurationAndSavesOnce()
        {
            var onDisk = Config(Bridge());
            onDisk.Profiles = new List<LightControlProfile> { new() { Name = "kept" }, null! };

            var plugin = Load(onDisk);

            plugin.Configuration.Profiles.Should().ContainSingle().Which.Name.Should().Be("kept");
            _written.Should().ContainSingle();
        }

        [Fact]
        public void Constructor_NullProfileBeforeTheBrightnessConversion_ConvertsTheOthers()
        {
            var onDisk = new PluginConfiguration
            {
                SchemaVersion = 0,
                Bridges = new List<HueBridge> { Bridge() },
                Profiles = new List<LightControlProfile> { null!, new() { Name = "p", PlayBrightness = 254 } }
            };

            var plugin = Load(onDisk);

            plugin.Configuration.Profiles.Should().ContainSingle().Which.PlayBrightness.Should().Be(100);
            plugin.Configuration.SchemaVersion.Should().Be(PluginConfiguration.CurrentSchemaVersion);
        }

        [Fact]
        public void UpdateConfiguration_DropsNullEntriesAndStillClearsAChangedBridgesPin()
        {
            var plugin = Load(Config(Bridge()));
            var incoming = Config(null!, Bridge(key: "newkey"));
            incoming.Profiles = new List<LightControlProfile> { null!, new() { Name = "p" } };

            plugin.UpdateConfiguration(incoming);

            _written.Should().ContainSingle().Which.Should().BeSameAs(incoming);
            incoming.Bridges.Should().ContainSingle().Which.HardwareId.Should().BeEmpty("the key changed, so the pin is cleared");
            incoming.Profiles.Should().ContainSingle().Which.Name.Should().Be("p");
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Once);
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
