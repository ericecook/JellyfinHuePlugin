using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    public class HueResourceCatalogTests
    {
        private static readonly string RoomUuid = "3883f8bf-30a3-445b-ac06-b047d50599df";

        private readonly Mock<HueService> _hue;
        private readonly HueResourceCatalog _catalog;
        private readonly HueBridge _bridge = new() { Id = "bridge1", Name = "Test Bridge", IpAddress = "192.168.1.50", Username = "key", BridgeId = "001788fffe123456" };

        private static readonly IReadOnlyList<HueGroupResource> Groups = new[]
        {
            new HueGroupResource("room-1", "gl-1", "Theater", "room", "/groups/1"),
            new HueGroupResource("zone-5", "gl-5", "Downstairs", "zone", "/groups/5"),
            new HueGroupResource("home-1", "gl-0", "All Lights", "bridge_home", "/groups/0")
        };

        private static readonly IReadOnlyList<HueSceneResource> Scenes = new[]
        {
            new HueSceneResource("sc-1", "Movie", "room-1", "/scenes/abc123"),
            new HueSceneResource("sc-2", "Bright", "zone-5", null)
        };

        public HueResourceCatalogTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(Groups);
            _hue.Setup(h => h.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(Scenes);
            _catalog = new HueResourceCatalog(_hue.Object, NullLogger.Instance);
        }

        private void VerifyGroupFetches(Times times) =>
            _hue.Verify(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), times);

        [Fact]
        public async Task ResolveGroupedLight_Uuid_PassesThroughWithoutFetching()
        {
            var id = await _catalog.ResolveGroupedLightAsync(_bridge, RoomUuid, CancellationToken.None);

            id.Should().Be(RoomUuid);
            VerifyGroupFetches(Times.Never());
        }

        [Theory]
        [InlineData("0")]
        [InlineData("")]
        public async Task ResolveGroupedLight_ZeroOrEmpty_MapsToBridgeHome(string target)
        {
            var id = await _catalog.ResolveGroupedLightAsync(_bridge, target, CancellationToken.None);

            id.Should().Be("gl-0");
        }

        [Theory]
        [InlineData("1", "gl-1")]
        [InlineData("5", "gl-5")]
        public async Task ResolveGroupedLight_V1Number_MapsThroughIdV1(string target, string expected)
        {
            var id = await _catalog.ResolveGroupedLightAsync(_bridge, target, CancellationToken.None);

            id.Should().Be(expected);
        }

        [Fact]
        public async Task ResolveScene_V1Id_MapsThroughIdV1()
        {
            var id = await _catalog.ResolveSceneAsync(_bridge, "abc123", CancellationToken.None);

            id.Should().Be("sc-1");
        }

        [Fact]
        public async Task ResolveScene_Uuid_PassesThrough()
        {
            var id = await _catalog.ResolveSceneAsync(_bridge, RoomUuid, CancellationToken.None);

            id.Should().Be(RoomUuid);
            VerifyGroupFetches(Times.Never());
        }

        [Fact]
        public async Task Resolve_Miss_RefreshesOnceThenReturnsNull()
        {
            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "9", CancellationToken.None);

            id.Should().BeNull();
            VerifyGroupFetches(Times.Exactly(2));
        }

        [Fact]
        public async Task Resolve_ServiceFailure_ReturnsNull()
        {
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueGroupResource>?)null);

            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "1", CancellationToken.None);

            id.Should().BeNull();
        }

        [Fact]
        public async Task SecondResolve_UsesTheCache()
        {
            await _catalog.ResolveGroupedLightAsync(_bridge, "1", CancellationToken.None);
            await _catalog.ResolveSceneAsync(_bridge, "abc123", CancellationToken.None);

            VerifyGroupFetches(Times.Once());
        }

        [Fact]
        public async Task ConcurrentResolves_FetchOnce()
        {
            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            var first = _catalog.ResolveGroupedLightAsync(_bridge, "1", CancellationToken.None);
            var second = _catalog.ResolveGroupedLightAsync(_bridge, "5", CancellationToken.None);
            gate.SetResult(Groups);
            var ids = await Task.WhenAll(first, second);

            ids.Should().Equal("gl-1", "gl-5");
            VerifyGroupFetches(Times.Once());
        }

        [Fact]
        public async Task ConcurrentResolves_OnAMiss_RefreshOnlyOnce()
        {
            // Seed the cache so both resolves start from a populated snapshot that misses for
            // both target ids, forcing each into the refresh path.
            await _catalog.GetGroupsAsync(_bridge, CancellationToken.None);

            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            var first = _catalog.ResolveGroupedLightAsync(_bridge, "9", CancellationToken.None);
            var second = _catalog.ResolveGroupedLightAsync(_bridge, "10", CancellationToken.None);
            gate.SetResult(Groups);
            var ids = await Task.WhenAll(first, second);

            ids.Should().Equal(new string?[] { null, null });
            VerifyGroupFetches(Times.Exactly(2)); // 1 seed load + 1 shared refresh, not 2 refreshes
        }

        [Fact]
        public async Task Invalidate_DuringInFlightRefresh_IsNotSilentlyUndone()
        {
            // Seed the cache so the "9" lookup below takes the miss -> refresh path rather than
            // the very first cold load.
            await _catalog.GetGroupsAsync(_bridge, CancellationToken.None);

            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            // "9" isn't in Groups, so this refreshes once and blocks on the gated fetch.
            var resolve = _catalog.ResolveGroupedLightAsync(_bridge, "9", CancellationToken.None);

            // Invalidate lands while that refresh is still in flight, waiting on the gate.
            _catalog.Invalidate(_bridge);

            // Let the in-flight (now stale-relative-to-the-invalidate) refresh finish.
            gate.SetResult(Groups);
            await resolve;

            // The invalidate must stick: the next read has to hit the bridge again rather than
            // serve the snapshot the in-flight refresh wrote after the invalidate landed.
            await _catalog.GetGroupsAsync(_bridge, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(3)); // seed + in-flight refresh + forced-by-invalidate refetch
        }

        [Fact]
        public async Task GetScenes_AreEnrichedWithGroupNameAndGroupedLight()
        {
            var scenes = await _catalog.GetScenesAsync(_bridge, CancellationToken.None);

            scenes.Should().NotBeNull();
            scenes![0].GroupName.Should().Be("Theater");
            scenes[0].GroupedLightId.Should().Be("gl-1");
            scenes[0].Group.Should().Be("Theater");
            scenes[1].GroupName.Should().Be("Downstairs");
        }

        [Fact]
        public async Task Invalidate_ForcesAFetchOnNextUse()
        {
            await _catalog.GetGroupsAsync(_bridge, CancellationToken.None);
            _catalog.Invalidate(_bridge);
            await _catalog.GetGroupsAsync(_bridge, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(2));
        }
    }
}
