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
    public sealed class HueControllerTests : IDisposable
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
        private readonly Mock<LightCommandExecutor> _executor;
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
            _executor = new Mock<LightCommandExecutor>(_hue.Object, _catalog.Object, NullLogger<LightCommandExecutor>.Instance) { CallBase = false };
            _config = new PluginConfiguration { EnablePlugin = true, Bridges = new List<HueBridge> { Bridge() } };
            _store = new FakeHueConfiguration(_config);
            _controller = new HueController(_log, _hue.Object, _catalog.Object, _store, _executor.Object);
        }

        private static T Value<T>(ActionResult<T> result) => ((OkObjectResult)result.Result!).Value.Should().BeOfType<T>().Subject;
        private static int Status<T>(ActionResult<T> result) => ((IStatusCodeActionResult)result.Result!).StatusCode!.Value;

        /// <summary>Every fact's log lines are checked here, not just the dedicated Authenticate test.</summary>
        public void Dispose()
        {
            _log.Lines.Should().NotContain(l => l.Contains(Key) || l.Contains("SECRET"));
        }

        [Fact]
        public async Task Authenticate_Failure_ReturnsTheServicesReason()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<AuthenticationOutcome>.Failure("The link button wasn't pressed. Press it on the bridge, then try again."));

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Be("The link button wasn't pressed. Press it on the bridge, then try again.");
            _store.SaveCount.Should().Be(0);
        }

        [Fact]
        public async Task Authenticate_NewAddress_CreatesAnEntry()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.70", It.IsAny<CancellationToken>())).ReturnsAsync(HueResult<AuthenticationOutcome>.Success(new AuthenticationOutcome("NEWKEY", HardwareId)));

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
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(HueResult<AuthenticationOutcome>.Success(new AuthenticationOutcome(Key, HardwareId)));

            var result = Value(await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50", BridgeId = "b1" }, CancellationToken.None));

            result.Id.Should().Be("b1");
            _config.Bridges.Should().HaveCount(1);
            _catalog.Verify(c => c.Invalidate(It.IsAny<HueBridge>()), Times.Never);
        }

        [Fact]
        public async Task Authenticate_ExistingByAddressWithNewKey_ClearsThenRelearnsThePin()
        {
            _hue.Setup(h => h.AuthenticateAsync("192.168.1.50", It.IsAny<CancellationToken>())).ReturnsAsync(HueResult<AuthenticationOutcome>.Success(new AuthenticationOutcome("ROTATED", "ffff88fffe000000")));
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
            _hue.Setup(h => h.AuthenticateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(HueResult<AuthenticationOutcome>.Success(new AuthenticationOutcome("SECRET-NEW", HardwareId)));

            await _controller.Authenticate(new AuthenticationRequest { BridgeIp = "192.168.1.50" }, CancellationToken.None);

            _log.Lines.Should().NotContain(l => l.Contains("SECRET") || l.Contains(Key));
        }

        [Theory]
        [InlineData("")]
        [InlineData("nope")]
        [InlineData("b2")]
        public async Task GetTargets_MissingUnknownOrKeylessBridge_Is400AndReadsNothing(string bridgeId)
        {
            _config.Bridges.Add(Bridge(id: "b2", key: ""));

            Status(await _controller.GetTargets(bridgeId, CancellationToken.None)).Should().Be(400);

            _catalog.Verify(c => c.ReadTargetsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task GetTargets_ReadFailure_Is500WithTheCatalogsReason()
        {
            _catalog.Setup(c => c.ReadTargetsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<HueTargets>.Failure("The bridge answered 503 Service Unavailable."));

            var response = await _controller.GetTargets("b1", CancellationToken.None);

            Status(response).Should().Be(500);
            ((ObjectResult)response.Result!).Value.Should().Be("The bridge answered 503 Service Unavailable.");
        }

        [Fact]
        public async Task GetTargets_KeysRoomsAndZonesByGroupedLightIdAndScenesById()
        {
            using var cts = new CancellationTokenSource();
            _catalog.Setup(c => c.ReadTargetsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(HueResult<HueTargets>.Success(new HueTargets(
                new[]
                {
                    new HueGroupResource("room-1", "gl-1", "Theater", "room"),
                    new HueGroupResource("zone-5", "gl-5", "Downstairs", "zone")
                },
                new[] { new HueSceneResource("sc-1", "Movie", "room-1") { GroupName = "Theater" } })));

            var targets = Value(await _controller.GetTargets("b1", cts.Token));

            targets.Groups.Keys.Should().BeEquivalentTo(new[] { "gl-1", "gl-5" });
            targets.Groups["gl-1"].Name.Should().Be("Theater");
            targets.Groups["gl-5"].Type.Should().Be("zone");
            targets.Scenes.Should().ContainKey("sc-1").WhoseValue.GroupName.Should().Be("Theater");
            _catalog.Verify(c => c.ReadTargetsAsync(_config.Bridges[0], cts.Token), Times.Once);
            _log.Lines.Should().Contain("API: Getting rooms, zones and scenes from bridge Bridge");
        }

        private static LightControlProfile TestProfile(string bridgeId = "b1") =>
            new() { Name = "Living Room", BridgeId = bridgeId, TargetGroupId = "gl-1", PlaySceneId = "", PlayBrightness = 55 };

        private void ExecutorReturns(LightCommandOutcome outcome, string? reason = null) =>
            _executor.Setup(e => e.ExecuteAsync(It.IsAny<LightAction>(), It.IsAny<HueBridge>(), It.IsAny<LightControlProfile>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LightCommandResult(outcome, reason));

        [Fact]
        public async Task TestLightControl_MissingOrUnknownActionOrMissingProfile_Is400()
        {
            Status(await _controller.TestLightControl(new TestLightRequest { Profile = TestProfile() }, CancellationToken.None)).Should().Be(400);
            Status(await _controller.TestLightControl(new TestLightRequest { Action = (LightAction)7, Profile = TestProfile() }, CancellationToken.None)).Should().Be(400);
            Status(await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Play }, CancellationToken.None)).Should().Be(400);

            _executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TestLightControl_UnknownBridge_ReportsItAndSendsNothing()
        {
            var result = Value(await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Play, Profile = TestProfile(bridgeId: "nope") }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Be("This profile has no bridge, or its bridge has no key.");
            _executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TestLightControl_EmptyBridgeId_UsesTheOnlyBridge()
        {
            ExecutorReturns(LightCommandOutcome.Succeeded);

            await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Stop, Profile = TestProfile(bridgeId: "") }, CancellationToken.None);

            _executor.Verify(e => e.ExecuteAsync(LightAction.Stop, _config.Bridges[0], It.IsAny<LightControlProfile>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task TestLightControl_EmptyBridgeIdWithTwoBridges_ReportsBridgeNotConfigured()
        {
            _config.Bridges.Add(Bridge(id: "b2"));

            var result = Value(await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Play, Profile = TestProfile(bridgeId: "") }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Be("This profile has no bridge, or its bridge has no key.");
            _executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TestLightControl_PassesTheRequestsActionProfileAndToken()
        {
            ExecutorReturns(LightCommandOutcome.Succeeded);
            using var cts = new CancellationTokenSource();
            var profile = TestProfile();

            await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Pause, Profile = profile }, cts.Token);

            _executor.Verify(e => e.ExecuteAsync(LightAction.Pause, _config.Bridges[0], It.Is<LightControlProfile>(p => ReferenceEquals(p, profile)), cts.Token), Times.Once);
        }

        [Theory]
        [InlineData(LightCommandOutcome.Succeeded, null, true, null)]
        [InlineData(LightCommandOutcome.BridgeNotConfigured, null, false, "This profile has no bridge, or its bridge has no key.")]
        [InlineData(LightCommandOutcome.SceneUnresolved, null, false, "That scene isn't on the bridge any more. Open the profile and pick it again.")]
        [InlineData(LightCommandOutcome.GroupUnresolved, null, false, "That room or zone isn't on the bridge any more. Open the profile and pick it again.")]
        [InlineData(LightCommandOutcome.Failed, "The bridge answered 503 Service Unavailable.", false, "The bridge answered 503 Service Unavailable.")]
        public async Task TestLightControl_MapsTheOutcome(LightCommandOutcome outcome, string? reason, bool success, string? error)
        {
            ExecutorReturns(outcome, reason);

            var result = Value(await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Play, Profile = TestProfile() }, CancellationToken.None));

            result.Success.Should().Be(success);
            result.Error.Should().Be(error);
        }

        [Fact]
        public async Task TestLightControl_LogsTheActionProfileAndBridge()
        {
            ExecutorReturns(LightCommandOutcome.Succeeded);

            await _controller.TestLightControl(new TestLightRequest { Action = LightAction.Stop, Profile = TestProfile() }, CancellationToken.None);

            _log.Lines.Should().Contain("API: Testing Stop for profile Living Room on bridge Bridge");
        }

        [Fact]
        public async Task VerifyConnection_NoAnswer_ReturnsTheServicesReasonUnprefixed()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<HueBridgeInfo>.Failure("The bridge at 192.168.1.50 didn't answer."));

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            // A whole sentence with no "Connection failed: " prefix (the design spec's Components
            // — server section): today's "Connection failed: Connection failed: …" is what that
            // prefix used to make possible.
            result.Error.Should().Be("The bridge at 192.168.1.50 didn't answer.");
        }

        [Fact]
        public async Task VerifyConnection_UnsupportedBridge_ReportsFactsAndFails()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<HueBridgeInfo>.Success(new HueBridgeInfo(HardwareId, "1940000000", "1.40.0", "BSB002")));

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.SupportsV2.Should().BeFalse();
            result.SoftwareVersion.Should().Be("1940000000");
            result.Error.Should().Be("Bridge software 1940000000 doesn't support the v2 API; 1948086000 or newer is required.");
            _hue.Verify(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task VerifyConnection_Success_PinsTheProbeToTheLearnedId()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<HueBridgeInfo>.Success(new HueBridgeInfo(HardwareId, "2071476020", "1.78.0", "BSB003")));
            HueBridge? probe = null;
            _hue.Setup(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, CancellationToken>((b, _) => probe = b)
                .ReturnsAsync(HueResult<IReadOnlyList<HueLightResource>>.Success(new[] { new HueLightResource("l1", "Lamp", true, 50) }));

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeTrue();
            result.HardwareId.Should().Be(HardwareId);
            result.ModelId.Should().Be("BSB003");
            probe!.HardwareId.Should().Be(HardwareId);
            probe.Username.Should().Be(Key);
        }

        [Fact]
        public async Task VerifyConnection_NoLights_ReturnsTheServicesReason()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<HueBridgeInfo>.Success(new HueBridgeInfo(HardwareId, "2071476020", "1.78.0", "BSB003")));
            _hue.Setup(h => h.GetLightsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(HueResult<IReadOnlyList<HueLightResource>>.Failure("The bridge's certificate doesn't match the stored one. Authenticate again to store the new one."));

            var result = Value(await _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, CancellationToken.None));

            result.Success.Should().BeFalse();
            result.Error.Should().Be("The bridge's certificate doesn't match the stored one. Authenticate again to store the new one.");
        }

        [Fact]
        public async Task VerifyConnection_CancelledToken_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            _hue.Setup(h => h.GetBridgeInfoAsync("192.168.1.50", cts.Token)).ThrowsAsync(new OperationCanceledException(cts.Token));

            await FluentActions.Awaiting(() => _controller.VerifyConnection(new VerifyConnectionRequest { BridgeIp = "192.168.1.50", Username = Key }, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Discover_PassesThrough()
        {
            var found = new List<HueBridgeDiscovery> { new() { Id = HardwareId, InternalIpAddress = "192.168.1.170" } };
            _hue.Setup(h => h.DiscoverBridgesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(found);

            Value(await _controller.DiscoverBridges(CancellationToken.None)).Should().BeSameAs(found);
        }
    }
}
