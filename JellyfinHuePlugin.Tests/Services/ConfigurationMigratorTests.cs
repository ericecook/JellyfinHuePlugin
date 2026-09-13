using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// The one-time rewrite of stored v1 ids, modelled on Eric's real configuration: a profile
    /// targeting v1 group 82 with v1 scene rI2iQLEIlwpr4uoU on a bridge whose HardwareId was
    /// never learned. The catalog and service are mocked through their virtual methods.
    /// </summary>
    public class ConfigurationMigratorTests
    {
        private const string HardwareId = "c42996fffec03a49";
        private const string LivingRoomGroupedLight = "da80a5cc-f037-4d4a-be1b-7238da9b4236";
        private const string ReadScene = "46629dbc-7c52-428f-96e8-0d752718ad09";
        private const string Key = "SECRETKEY0123456789";

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }

        private static readonly IReadOnlyList<HueGroupResource> Groups = new[]
        {
            new HueGroupResource("7da38a46-92a9-4665-8dd2-f6eb324e1531", LivingRoomGroupedLight, "Living room", "room", "/groups/82"),
            new HueGroupResource("6a39331b-ac46-4c4b-ab50-b4d379c96982", "5c41a487-d842-4123-bf73-8032454b06db", "All Lights", "bridge_home", "/groups/0")
        };

        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly CapturingLogger _log = new();
        private readonly ConfigurationMigrator _migrator;

        public ConfigurationMigratorTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.GetBridgeInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HueBridgeInfo(HardwareId, "2071476020", "1.78.0", "BSB003"));

            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger.Instance) { CallBase = false };
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(Groups);
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), "82", It.IsAny<CancellationToken>())).ReturnsAsync(LivingRoomGroupedLight);
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), "rI2iQLEIlwpr4uoU", It.IsAny<CancellationToken>())).ReturnsAsync(ReadScene);

            _migrator = new ConfigurationMigrator(_hue.Object, _catalog.Object, _log);
        }

        private static HueBridge Bridge(string id = "b1", string hardwareId = "", string key = Key) =>
            new() { Id = id, Name = "Bridge " + id, IpAddress = "192.168.1.170", Username = key, HardwareId = hardwareId };

        private static LightControlProfile Profile(string target = "82", string play = "", string pause = "rI2iQLEIlwpr4uoU", string stop = "rI2iQLEIlwpr4uoU", string bridgeId = "b1") =>
            new() { Id = "p1", Name = "Living Room HTPC", BridgeId = bridgeId, TargetGroupId = target, PlaySceneId = play, PauseSceneId = pause, StopSceneId = stop };

        private static PluginConfiguration Config(HueBridge bridge, params LightControlProfile[] profiles)
        {
            var config = new PluginConfiguration();
            config.Bridges.Add(bridge);
            config.Profiles.AddRange(profiles);
            return config;
        }

        [Fact]
        public async Task V1Ids_AreRewrittenToV2IdsAndReported()
        {
            var profile = Profile();
            var config = Config(Bridge(), profile);

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            profile.TargetGroupId.Should().Be(LivingRoomGroupedLight);
            profile.PauseSceneId.Should().Be(ReadScene);
            profile.StopSceneId.Should().Be(ReadScene);
            profile.PlaySceneId.Should().BeEmpty();
            report.Changed.Should().BeTrue();
            var entry = report.Profiles.Should().ContainSingle().Subject;
            entry.Status.Should().Be(MigrationStatus.Ok);
            entry.Rewritten.Should().Equal("TargetGroupId", "PauseSceneId", "StopSceneId");
            entry.Unresolved.Should().BeEmpty();
            _log.Lines.Should().Contain("Rewrote TargetGroupId of profile Living Room HTPC from 82 to " + LivingRoomGroupedLight);
        }

        [Fact]
        public async Task UuidsAndZero_AreLeftAloneWithoutResolving()
        {
            var profile = Profile(target: "0", pause: ReadScene, stop: "");
            var config = Config(Bridge(hardwareId: HardwareId), profile);

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            profile.TargetGroupId.Should().Be("0");
            profile.PauseSceneId.Should().Be(ReadScene);
            report.Changed.Should().BeFalse();
            report.Profiles.Single().Rewritten.Should().BeEmpty();
            _catalog.Verify(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
            _catalog.Verify(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task UnresolvedId_IsKeptAndReported()
        {
            var profile = Profile(target: "99");
            var config = Config(Bridge(hardwareId: HardwareId), profile);

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            profile.TargetGroupId.Should().Be("99");
            profile.PauseSceneId.Should().Be(ReadScene, "the other fields are still rewritten");
            var entry = report.Profiles.Single();
            entry.Unresolved.Should().Equal("TargetGroupId");
            entry.Rewritten.Should().Equal("PauseSceneId", "StopSceneId");
            report.Changed.Should().BeTrue();
        }

        [Fact]
        public async Task UnreachableBridge_SkipsItsProfilesAndIsReported()
        {
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueGroupResource>?)null);
            var profile = Profile();
            var config = Config(Bridge(hardwareId: HardwareId), profile);

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            profile.TargetGroupId.Should().Be("82");
            report.Bridges.Single().Status.Should().Be(MigrationStatus.Unreachable);
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Skipped);
            report.Changed.Should().BeFalse();
            _log.Lines.Should().Contain("Bridge Bridge b1 could not be reached during migration; its profiles were not checked");
        }

        [Fact]
        public async Task UnauthenticatedBridge_IsReportedAndItsProfilesSkipped()
        {
            var config = Config(Bridge(key: ""), Profile());

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            report.Bridges.Single().Status.Should().Be(MigrationStatus.Unauthenticated);
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Skipped);
            _hue.Verify(h => h.GetBridgeInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
            _catalog.Verify(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task EmptyHardwareId_IsLearnedFromTheBridge()
        {
            var bridge = Bridge();
            var config = Config(bridge);

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            bridge.HardwareId.Should().Be(HardwareId);
            report.Bridges.Single().HardwareIdLearned.Should().BeTrue();
            report.Changed.Should().BeTrue();
            _log.Lines.Should().Contain("Learned bridge id c42996fffec03a49 for bridge Bridge b1");
        }

        [Fact]
        public async Task KnownHardwareId_IsNotFetchedAgain()
        {
            var config = Config(Bridge(hardwareId: HardwareId));

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            report.Bridges.Single().HardwareIdLearned.Should().BeFalse();
            report.Changed.Should().BeFalse();
            _hue.Verify(h => h.GetBridgeInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task BridgeInfoFailure_StillMigratesWhenTheCatalogLoads()
        {
            _hue.Setup(h => h.GetBridgeInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((HueBridgeInfo?)null);
            var bridge = Bridge();
            var profile = Profile();

            var report = await _migrator.MigrateAsync(Config(bridge, profile), CancellationToken.None);

            bridge.HardwareId.Should().BeEmpty();
            report.Bridges.Single().Status.Should().Be(MigrationStatus.Ok);
            report.Bridges.Single().HardwareIdLearned.Should().BeFalse();
            profile.TargetGroupId.Should().Be(LivingRoomGroupedLight);
        }

        [Fact]
        public async Task ProfileWithoutABridgeId_UsesTheOnlyBridge()
        {
            var profile = Profile(bridgeId: "");

            var report = await _migrator.MigrateAsync(Config(Bridge(hardwareId: HardwareId), profile), CancellationToken.None);

            profile.TargetGroupId.Should().Be(LivingRoomGroupedLight);
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Ok);
        }

        [Fact]
        public async Task ProfileWithoutABridgeId_AmongSeveralBridges_IsSkipped()
        {
            var profile = Profile(bridgeId: "");
            var config = Config(Bridge(hardwareId: HardwareId), profile);
            config.Bridges.Add(Bridge(id: "b2", hardwareId: HardwareId));

            var report = await _migrator.MigrateAsync(config, CancellationToken.None);

            profile.TargetGroupId.Should().Be("82");
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Skipped);
        }

        [Fact]
        public async Task ProfileWithAnUnknownBridgeId_IsSkipped()
        {
            var profile = Profile(bridgeId: "nope");

            var report = await _migrator.MigrateAsync(Config(Bridge(hardwareId: HardwareId), profile), CancellationToken.None);

            profile.TargetGroupId.Should().Be("82");
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Skipped);
        }

        [Fact]
        public async Task TheCatalogIsInvalidatedBeforeAnythingIsFetched()
        {
            var invalidated = false;
            _catalog.Setup(c => c.InvalidateAll()).Callback(() => invalidated = true);
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    invalidated.Should().BeTrue("a page load must see fresh rooms and scenes");
                    return Task.FromResult<IReadOnlyList<HueGroupResource>?>(Groups);
                });

            await _migrator.MigrateAsync(Config(Bridge(hardwareId: HardwareId), Profile()), CancellationToken.None);

            _catalog.Verify(c => c.InvalidateAll(), Times.Once());
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()))
                .Returns((HueBridge _, CancellationToken ct) => Task.FromCanceled<IReadOnlyList<HueGroupResource>?>(ct));

            await FluentActions.Awaiting(() => _migrator.MigrateAsync(Config(Bridge(hardwareId: HardwareId), Profile()), cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task AThrowingBridge_IsReportedUnreachableAndTheKeyIsNeverLogged()
        {
            _catalog.Setup(c => c.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

            var report = await _migrator.MigrateAsync(Config(Bridge(hardwareId: HardwareId), Profile()), CancellationToken.None);

            report.Bridges.Single().Status.Should().Be(MigrationStatus.Unreachable);
            report.Profiles.Single().Status.Should().Be(MigrationStatus.Skipped);
            _log.Lines.Should().NotContain(line => line.Contains(Key));
        }
    }
}
