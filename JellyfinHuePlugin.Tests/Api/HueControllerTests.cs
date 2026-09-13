using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using JellyfinHuePlugin.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Api
{
    /// <summary>
    /// The page ↔ controller contract, endpoint by endpoint, over mocked services. The response
    /// shapes here are what configPage.html reads; see PageContractTests for the names.
    /// </summary>
    public class HueControllerTests : IDisposable
    {
        private const string Key = "SECRETKEY0123456789";
        private const string HardwareId = "c42996fffec03a49";

        private sealed class CapturingLogger : ILogger<HueController>
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }

        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly Mock<ConfigurationMigrator> _migrator;
        private readonly CapturingLogger _log = new();
        private readonly PluginConfiguration _config;
        private readonly FakeHueConfiguration _store;
        private readonly HueController _controller;

        private static HueBridge Bridge(string id = "b1", string key = Key) =>
            new() { Id = id, Name = "Bridge", IpAddress = "192.168.1.50", Username = key, HardwareId = HardwareId };

        public HueControllerTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>(), new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance)) { CallBase = false };
            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger<HueResourceCatalog>.Instance) { CallBase = false };
            _migrator = new Mock<ConfigurationMigrator>(_hue.Object, _catalog.Object, NullLogger<ConfigurationMigrator>.Instance) { CallBase = false };
            _config = new PluginConfiguration { EnablePlugin = true, Bridges = new List<HueBridge> { Bridge() } };
            _store = new FakeHueConfiguration(_config);
            _controller = new HueController(_log, _hue.Object, _catalog.Object, _migrator.Object, _store);
        }

        private static T Value<T>(ActionResult<T> result) => ((OkObjectResult)result.Result!).Value.Should().BeOfType<T>().Subject;
        private static int Status(ActionResult result) => ((IStatusCodeActionResult)result).StatusCode!.Value;
        private static int Status<T>(ActionResult<T> result) => ((IStatusCodeActionResult)result.Result!).StatusCode!.Value;

        /// <summary>Every fact's log lines are checked here, not just the dedicated Authenticate test.</summary>
        public void Dispose()
        {
            _log.Lines.Should().NotContain(l => l.Contains(Key) || l.Contains("SECRET"));
        }

        [Fact]
        public void GetBridges_ReportsAuthenticationWithoutTheKey()
        {
            _config.Bridges.Add(Bridge(id: "b2", key: ""));

            var bridges = Value(_controller.GetBridges());

            bridges.Select(b => (b.Id, b.IsAuthenticated)).Should().Equal(("b1", true), ("b2", false));
            typeof(BridgeInfo).GetProperty("Username").Should().BeNull();
        }

        [Fact]
        public void AddBridge_AppendsSavesAndReturnsTheNewEntry()
        {
            var info = Value(_controller.AddBridge(new AddBridgeRequest { IpAddress = "192.168.1.60", Name = "Upstairs" }));

            _config.Bridges.Should().HaveCount(2);
            info.Id.Should().Be(_config.Bridges[1].Id).And.NotBeNullOrEmpty();
            info.IsAuthenticated.Should().BeFalse();
            _store.SaveCount.Should().Be(1);
        }

        [Fact]
        public void DeleteBridge_Unknown_Is404AndDoesNotSave()
        {
            Status(_controller.DeleteBridge("nope")).Should().Be(404);
            _store.SaveCount.Should().Be(0);
        }

        [Fact]
        public void DeleteBridge_Known_RemovesInvalidatesAndSaves()
        {
            var bridge = _config.Bridges[0];

            Status(_controller.DeleteBridge("b1")).Should().Be(200);

            _config.Bridges.Should().BeEmpty();
            _catalog.Verify(c => c.Invalidate(bridge), Times.Once);
            _store.SaveCount.Should().Be(1);
        }

        [Fact]
        public async Task Migrate_SavesOnlyWhenChanged()
        {
            var report = new MigrationReport { Changed = false };
            _migrator.Setup(m => m.MigrateAsync(_config, It.IsAny<CancellationToken>())).ReturnsAsync(report);

            Value(await _controller.Migrate(CancellationToken.None)).Should().BeSameAs(report);
            _store.SaveCount.Should().Be(0);

            report.Changed = true;
            await _controller.Migrate(CancellationToken.None);
            _store.SaveCount.Should().Be(1);
        }

        [Fact]
        public async Task Authenticate_Failure_ReturnsTheGuidanceText()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationOutcome?)null);

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("Press the link button");
            _store.SaveCount.Should().Be(0);
        }

        [Fact]
        public async Task Authenticate_NewAddress_CreatesAnEntry()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.70", It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticationOutcome("NEWKEY", HardwareId));

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.70", BridgeName = "Attic" }, CancellationToken.None));

            result.Success.Should().BeTrue();
            result.Username.Should().Be("NEWKEY");
            result.HardwareId.Should().Be(HardwareId);
            var added = _config.Bridges.Single(b => b.IpAddress == "192.168.1.70");
            result.Id.Should().Be(added.Id);
            added.Name.Should().Be("Attic");
            added.Username.Should().Be("NEWKEY");
            _store.SaveCount.Should().Be(1);
        }

        [Fact]
        public async Task Authenticate_ExistingById_UpdatesInPlace()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticationOutcome(Key, HardwareId));

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50", BridgeId = "b1" }, CancellationToken.None));

            result.Id.Should().Be("b1");
            _config.Bridges.Should().HaveCount(1);
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Never);
        }

        [Fact]
        public async Task Authenticate_ExistingByAddressWithNewKey_ClearsThenRelearnsThePin()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticationOutcome("ROTATED", "ffff88fffe000000"));
            string? keyAtInvalidation = null;
            _catalog.Setup(c => c.Invalidate(It.IsAny<HueBridge>())).Callback<HueBridge>(b => keyAtInvalidation = b.Username);

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None));

            result.Id.Should().Be("b1");
            _config.Bridges[0].Username.Should().Be("ROTATED");
            _config.Bridges[0].HardwareId.Should().Be("ffff88fffe000000");
            // The catalog's cache key is address + key: Invalidate must fire before Username is
            // rewritten, or it invalidates the entry under the NEW key and leaves the old one cached.
            keyAtInvalidation.Should().Be(Key);
            _catalog.Verify(c => c.Invalidate(_config.Bridges[0]), Times.Once);
            _log.Lines.Should().Contain(l => l.Contains("changed address or application key"));
        }

        [Fact]
        public async Task Authenticate_NeverLogsTheKey()
        {
            _hue.Setup(h => h.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AuthenticationOutcome("SECRET-NEW", HardwareId));

            await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None);

            _log.Lines.Should().NotContain(l => l.Contains("SECRET") || l.Contains(Key));
        }

        [Fact]
        public async Task GetGroups_Unconfigured_Is400()
        {
            _config.Bridges[0].Username = "";

            Status(await _controller.GetGroups("b1", CancellationToken.None)).Should().Be(400);
        }

        [Fact]
        public async Task GetGroups_CatalogFailure_Is500()
        {
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueGroupResource>?)null);

            Status(await _controller.GetGroups("b1", CancellationToken.None)).Should().Be(500);
        }

        [Fact]
        public async Task GetGroups_KeysByGroupedLightIdAndDropsBridgeHome()
        {
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new HueGroupResource("room-1", "gl-1", "Theater", "room", "/groups/1"),
                new HueGroupResource("zone-5", "gl-5", "Downstairs", "zone", "/groups/5"),
                new HueGroupResource("home-1", "gl-0", "All Lights", "bridge_home", "/groups/0")
            });

            var groups = Value(await _controller.GetGroups("b1", CancellationToken.None));

            groups.Keys.Should().BeEquivalentTo(new[] { "gl-1", "gl-5" });
            groups["gl-1"].Name.Should().Be("Theater");
            groups["gl-5"].Type.Should().Be("zone");
        }

        [Fact]
        public async Task GetScenes_KeysBySceneId()
        {
            _catalog.Setup(c => c.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new HueSceneResource("sc-1", "Movie", "room-1", "/scenes/abc") { GroupName = "Theater" }
            });

            var scenes = Value(await _controller.GetScenes("b1", CancellationToken.None));

            scenes.Should().ContainKey("sc-1").WhoseValue.GroupName.Should().Be("Theater");
        }

        [Fact]
        public async Task GetScenes_CatalogFailure_Is500()
        {
            _catalog.Setup(c => c.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueSceneResource>?)null);

            Status(await _controller.GetScenes("b1", CancellationToken.None)).Should().Be(500);
        }

        [Fact]
        public async Task TestLightControl_Unconfigured_Is400()
        {
            _config.Bridges[0].Username = "";

            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1" }, CancellationToken.None)).Should().Be(400);
        }

        [Fact]
        public async Task TestLightControl_TurnOff_SendsOffWithoutBrightness()
        {
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), "gl-1", It.IsAny<CancellationToken>())).ReturnsAsync("gl-1");
            GroupedLightState? sent = null;
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), "gl-1", It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, s, _) => sent = s).ReturnsAsync(true);

            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1", GroupId = "gl-1", TurnOff = true, Brightness = 50 }, CancellationToken.None)).Should().Be(200);

            sent!.On.Should().BeFalse();
            sent.Brightness.Should().BeNull();
            sent.DurationMs.Should().Be(1000);
        }

        [Fact]
        public async Task TestLightControl_Brightness_IsClampedAndOn()
        {
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), "0", It.IsAny<CancellationToken>())).ReturnsAsync("gl-0");
            GroupedLightState? sent = null;
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), "gl-0", It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, s, _) => sent = s).ReturnsAsync(true);

            await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1", Brightness = 250 }, CancellationToken.None);

            sent!.On.Should().BeTrue();
            sent.Brightness.Should().Be(100);
        }

        [Fact]
        public async Task TestLightControl_Scene_RecallsWithOneSecond()
        {
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), "abc", It.IsAny<CancellationToken>())).ReturnsAsync("sc-1");
            _hue.Setup(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), "sc-1", 1000, It.IsAny<CancellationToken>())).ReturnsAsync(true);

            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1", SceneId = "abc" }, CancellationToken.None)).Should().Be(200);
        }

        [Fact]
        public async Task TestLightControl_UnresolvedTargets_Are500()
        {
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1", SceneId = "gone" }, CancellationToken.None)).Should().Be(500);
            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1", GroupId = "gone" }, CancellationToken.None)).Should().Be(500);
        }

        [Fact]
        public async Task TestLightControl_ServiceFailure_Is500()
        {
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), "0", It.IsAny<CancellationToken>())).ReturnsAsync("gl-0");
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), "gl-0", It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

            Status(await _controller.TestLightControl(new TestLightRequest { BridgeId = "b1" }, CancellationToken.None)).Should().Be(500);
        }

        [Fact]
        public async Task VerifyConnection_NoAnswer_FailsWithGuidance()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync((HueBridgeInfo?)null);

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("/api/0/config");
        }

        [Fact]
        public async Task VerifyConnection_UnsupportedBridge_ReportsFactsAndFails()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(new HueBridgeInfo(HardwareId, "1940000000", "1.40.0", "BSB002"));

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.SupportsV2.Should().BeFalse();
            result.SoftwareVersion.Should().Be("1940000000");
            _hue.Verify(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task VerifyConnection_Success_PinsTheProbeToTheLearnedId()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(new HueBridgeInfo(HardwareId, "2071476020", "1.78.0", "BSB003"));
            HueBridge? probe = null;
            _hue.Setup(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, CancellationToken>((b, _) => probe = b)
                .ReturnsAsync(new[] { new HueLightResource("l1", "Lamp", true, 50, null) });

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeTrue();
            result.HardwareId.Should().Be(HardwareId);
            result.ModelId.Should().Be("BSB003");
            probe!.HardwareId.Should().Be(HardwareId);
            probe.Username.Should().Be(Key);
        }

        [Fact]
        public async Task VerifyConnection_NoLights_NamesTheCertificateAsAPossibleCause()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(new HueBridgeInfo(HardwareId, "2071476020", "1.78.0", "BSB003"));
            _hue.Setup(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueLightResource>?)null);

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("certificate");
        }

        [Fact]
        public async Task Discover_PassesThrough()
        {
            var found = new List<HueBridgeDiscovery> { new() { Id = HardwareId, InternalIpAddress = "192.168.1.170" } };
            _hue.Setup(h => h.DiscoverBridgesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(found);

            Value(await _controller.DiscoverBridges(CancellationToken.None)).Should().BeSameAs(found);
        }

        [Fact]
        public async Task TestBridgeConnection_PassesThrough()
        {
            _hue.Setup(h => h.TestBridgeConnectionAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync("Bridge ok");

            var ok = (OkObjectResult)(await _controller.TestBridgeConnection(new TestConnectionRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None)).Result!;

            ok.Value!.ToString().Should().Contain("Bridge ok");
        }
    }
}
