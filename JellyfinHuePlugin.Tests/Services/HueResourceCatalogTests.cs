using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    public class HueResourceCatalogTests
    {
        private static readonly string RoomUuid = "3883f8bf-30a3-445b-ac06-b047d50599df";

        private sealed class CapturingLogger : ILogger<HueResourceCatalog>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();
            public IEnumerable<string> Lines => Entries.Select(e => e.Message);
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        private readonly Mock<HueService> _hue;
        private readonly CapturingLogger _log = new();
        private readonly HueResourceCatalog _catalog;
        private readonly HueBridge _bridge = new() { Id = "bridge1", Name = "Test Bridge", IpAddress = "192.168.1.50", Username = "key", HardwareId = "001788fffe123456" };

        private static readonly IReadOnlyList<HueGroupResource> Groups = new[]
        {
            new HueGroupResource("room-1", "gl-1", "Theater", "room"),
            new HueGroupResource("zone-5", "gl-5", "Downstairs", "zone"),
            new HueGroupResource("home-1", "gl-0", "All Lights", "bridge_home")
        };

        private static readonly IReadOnlyList<HueGroupResource> GroupsWithoutHome = new[]
        {
            new HueGroupResource("room-1", "gl-1", "Theater", "room")
        };

        private static readonly IReadOnlyList<HueSceneResource> Scenes = new[]
        {
            new HueSceneResource("sc-1", "Movie", "room-1"),
            new HueSceneResource("sc-2", "Bright", "zone-5")
        };

        public HueResourceCatalogTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>(), new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance)) { CallBase = false };
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(Groups);
            _hue.Setup(h => h.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(Scenes);
            _catalog = new HueResourceCatalog(_hue.Object, _log);
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

        [Fact]
        public async Task ResolveGroupedLight_NotAUuidOrZero_ReturnsNullWithReselectAdviceWithoutFetching()
        {
            // A group number stored before 4.0 is no longer looked up through id_v1.
            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "9", CancellationToken.None);

            id.Should().BeNull();
            _log.Lines.Should().ContainSingle()
                .Which.Should().Be("Profile target group '9' not found on bridge Test Bridge; re-select it on the plugin page");
            VerifyGroupFetches(Times.Never());
        }

        [Fact]
        public async Task ResolveGroupedLight_ZeroWithoutBridgeHome_ReturnsNullAfterOneFetch()
        {
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync(GroupsWithoutHome);

            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);

            id.Should().BeNull();
            _log.Lines.Should().ContainSingle()
                .Which.Should().Be("Profile target group '0' not found on bridge Test Bridge; re-select it on the plugin page");
            VerifyGroupFetches(Times.Once()); // a miss no longer triggers a second, refreshing fetch
        }

        [Fact]
        public async Task ResolveScene_Uuid_PassesThrough()
        {
            var id = await _catalog.ResolveSceneAsync(_bridge, RoomUuid, CancellationToken.None);

            id.Should().Be(RoomUuid);
            VerifyGroupFetches(Times.Never());
        }

        [Fact]
        public async Task ResolveScene_NotAUuid_ReturnsNullWithReselectAdviceWithoutFetching()
        {
            // A scene id stored before 4.0 is no longer looked up through id_v1.
            var id = await _catalog.ResolveSceneAsync(_bridge, "abc123", CancellationToken.None);

            id.Should().BeNull();
            _log.Lines.Should().ContainSingle()
                .Which.Should().Be("Profile target scene 'abc123' not found on bridge Test Bridge; re-select it on the plugin page");
            VerifyGroupFetches(Times.Never());
            _hue.Verify(h => h.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task Resolve_ServiceFailure_ReturnsNull()
        {
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueGroupResource>?)null);

            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);

            id.Should().BeNull();
        }

        [Fact]
        public async Task Resolve_WhenTheGroupFetchFails_SaysTheBridgeIsUnreachableAndDoesNotSayReselect()
        {
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueGroupResource>?)null);

            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);

            id.Should().BeNull();
            var line = _log.Lines.Should().ContainSingle().Subject;
            line.Should().Contain("Could not reach bridge Test Bridge");
            line.Should().NotContain("re-select", "an unreachable bridge is not something re-selecting the target fixes");
        }

        [Fact]
        public async Task ResolveGroupedLight_WhenTheSceneFetchFails_SaysTheBridgeIsUnreachable()
        {
            // Groups and scenes load together, so a failed scene fetch leaves no bridge home either.
            _hue.Setup(h => h.GetScenesAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<HueSceneResource>?)null);

            var id = await _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);

            id.Should().BeNull();
            var line = _log.Lines.Should().ContainSingle().Subject;
            line.Should().Contain("Could not reach bridge Test Bridge");
            line.Should().NotContain("re-select");
        }

        [Fact]
        public async Task SecondResolve_UsesTheCache()
        {
            await _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);
            await _catalog.ResolveGroupedLightAsync(_bridge, "", CancellationToken.None);

            VerifyGroupFetches(Times.Once());
        }

        [Fact]
        public async Task ConcurrentResolves_FetchOnce()
        {
            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            var first = _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);
            var second = _catalog.ResolveGroupedLightAsync(_bridge, "", CancellationToken.None);
            gate.SetResult(Groups);
            var ids = await Task.WhenAll(first, second);

            ids.Should().Equal("gl-0", "gl-0");
            VerifyGroupFetches(Times.Once());
        }

        [Fact]
        public async Task Invalidate_DuringInFlightRefresh_IsNotSilentlyUndone()
        {
            // Seed the cache so the call below is a refresh of a populated snapshot.
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            // The editor's refresh blocks on the gated fetch.
            var inFlight = _catalog.GetGroupsAsync(_bridge, refresh: true, CancellationToken.None);

            // Invalidate lands while that refresh is still in flight, waiting on the gate.
            _catalog.Invalidate(_bridge);

            // Let the in-flight (now stale-relative-to-the-invalidate) refresh finish.
            gate.SetResult(Groups);
            await inFlight;

            // The invalidate must stick: the next read has to hit the bridge again rather than
            // serve the snapshot the in-flight refresh would have written after the invalidate landed.
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(3)); // seed + in-flight refresh + forced-by-invalidate refetch
        }

        [Fact]
        public async Task Resolve_WhenInvalidateLandsDuringTheInFlightFetch_LogsDebugInsteadOfReselect()
        {
            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            // A cold cache: resolving the bridge home loads it and blocks on the gated fetch.
            var resolve = _catalog.ResolveGroupedLightAsync(_bridge, "0", CancellationToken.None);

            // Invalidate lands while that load is still in flight: the fetch's eventual
            // result is discarded, and the resolve must not blame "0" for that.
            _catalog.Invalidate(_bridge);

            gate.SetResult(Groups);
            var id = await resolve;

            id.Should().BeNull();
            _log.Entries.Should().ContainSingle();
            var entry = _log.Entries[0];
            entry.Level.Should().Be(LogLevel.Debug, "nothing is wrong - the cache clear is transient and the next resolve retries on its own");
            entry.Message.Should().NotContain("re-select", "the bridge home was never actually looked up against real bridge data");
            entry.Message.Should().Contain("Test Bridge");
        }

        [Fact]
        public async Task Invalidate_WhileAPeerRefreshIsQueuedOnTheGate_QueuedCallStillFetches()
        {
            // Seed the cache so both calls below are refreshes of a populated snapshot.
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            // The holder refreshes and blocks on the gate holding it the whole time.
            var holder = _catalog.GetGroupsAsync(_bridge, refresh: true, CancellationToken.None);

            // A second refresh has to queue behind the holder's gate.
            var queued = _catalog.GetGroupsAsync(_bridge, refresh: true, CancellationToken.None);

            // Invalidate lands while the second is queued, i.e. after it captured the generation but
            // before it ever reached the gate-holding refresh's post-fetch check.
            _catalog.Invalidate(_bridge);

            gate.SetResult(Groups);
            await Task.WhenAll(holder, queued);

            // The holder's fetch was discarded, so the queued call must not reuse the invalidate's
            // null result: seed + holder's discarded refresh + the queued call's own refresh.
            (await queued).Should().NotBeNull();
            VerifyGroupFetches(Times.Exactly(3));
        }

        [Fact]
        public async Task GetGroups_Refresh_FetchesEvenWhenCached()
        {
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);
            VerifyGroupFetches(Times.Once());

            await _catalog.GetGroupsAsync(_bridge, refresh: true, CancellationToken.None);
            VerifyGroupFetches(Times.Exactly(2));
        }

        [Fact]
        public async Task ConcurrentGroupAndSceneRefreshes_ShareOneFetch()
        {
            // The profile editor asks for groups and scenes at once, both with refresh=true.
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            var gate = new TaskCompletionSource<IReadOnlyList<HueGroupResource>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.GetGroupsAsync(It.IsAny<HueBridge>(), It.IsAny<CancellationToken>())).Returns(gate.Task);

            var groups = _catalog.GetGroupsAsync(_bridge, refresh: true, CancellationToken.None);
            var scenes = _catalog.GetScenesAsync(_bridge, refresh: true, CancellationToken.None);
            gate.SetResult(Groups);
            await Task.WhenAll(groups, scenes);

            (await groups).Should().NotBeNull();
            (await scenes).Should().NotBeNull();
            VerifyGroupFetches(Times.Exactly(2)); // 1 seed load + 1 shared refresh, not 2 refreshes
        }

        [Fact]
        public async Task GetScenes_AreEnrichedWithGroupName()
        {
            var scenes = await _catalog.GetScenesAsync(_bridge, refresh: false, CancellationToken.None);

            scenes.Should().NotBeNull();
            scenes![0].GroupName.Should().Be("Theater");
            scenes[0].Group.Should().Be("Theater");
            scenes[1].GroupName.Should().Be("Downstairs");
        }

        [Fact]
        public async Task RepointingABridge_MissesTheCacheRatherThanServingTheOldBridgesCatalog()
        {
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            // The admin edits the bridge: same configuration entry (same Id), different hardware.
            _bridge.IpAddress = "192.168.1.99";
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(2));

            // A new application key means a different view of the bridge as well.
            _bridge.Username = "another-key";
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(3));
        }

        [Fact]
        public async Task Invalidate_FindsTheEntryThroughTheSameComposedKeyTheLoadPathUsed()
        {
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            // A HueBridge is a configuration record, not an identity: the caller that invalidates
            // holds its own instance, so the key has to be composed from the values, and both
            // sites have to compose it the same way or Invalidate quietly does nothing.
            var sameBridge = new HueBridge
            {
                Id = _bridge.Id,
                Name = _bridge.Name,
                IpAddress = _bridge.IpAddress,
                Username = _bridge.Username,
                HardwareId = _bridge.HardwareId
            };
            _catalog.Invalidate(sameBridge);

            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(2));
        }

        [Fact]
        public async Task Invalidate_ForcesAFetchOnNextUse()
        {
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);
            _catalog.Invalidate(_bridge);
            await _catalog.GetGroupsAsync(_bridge, refresh: false, CancellationToken.None);

            VerifyGroupFetches(Times.Exactly(2));
        }
    }

    /// <summary>
    /// Direct tests of HueResourceCatalog.Entry's (Snapshot, Generation) synchronization
    /// contract - the mechanism that keeps a concurrent Invalidate from racing a write. Entry is
    /// internal (not private) specifically so this is possible, the same way HueService exposes
    /// ShouldAcceptBridgeCertificate for its own otherwise-untestable concurrency seam.
    ///
    /// These are white-box by necessity, not by choice: a resolve racing a single Invalidate
    /// through HueResourceCatalog's public API is observationally identical whether the race
    /// resolves correctly or not, because a discarded write and a legitimate later write produce
    /// the exact same content (the mocked bridge always returns the same data) and the exact
    /// same fetch count (Invalidate unconditionally clears the entry regardless of who "wins", so
    /// by the time both sides of the race have completed, the state is indistinguishable from a
    /// plain sequential invalidate-then-refetch). Only Entry's own return values - which pin
    /// exactly which generation a store was attempted against - can tell the two apart. See the
    /// task report for the full reasoning and the black-box attempts that were ruled out.
    /// </summary>
    public class HueResourceCatalogEntryTests
    {
        private static HueResourceCatalog.Snapshot MakeSnapshot(string tag) =>
            new(new[] { new HueGroupResource(tag, tag, tag, "room") }, Array.Empty<HueSceneResource>());

        [Fact]
        public void TryStore_WithAStaleGeneration_IsRejectedAndWritesNothing()
        {
            var entry = new HueResourceCatalog.Entry();
            var (_, generation) = entry.ReadState(); // 0: what a caller would have captured before fetching

            entry.Invalidate(); // a concurrent Invalidate lands before that caller's store

            var stored = entry.TryStore(MakeSnapshot("stale"), generation);

            stored.Should().BeFalse("the generation moved since this snapshot's fetch started, so the store must be refused");
            entry.ReadState().Snapshot.Should().BeNull("a rejected store must not have written anything");
        }

        [Fact]
        public void TryStore_WithTheCurrentGeneration_SucceedsAndAdvancesTheGeneration()
        {
            var entry = new HueResourceCatalog.Entry();
            var (_, generation) = entry.ReadState();

            var stored = entry.TryStore(MakeSnapshot("fresh"), generation);

            stored.Should().BeTrue();
            var (snapshot, newGeneration) = entry.ReadState();
            snapshot!.Groups[0].Id.Should().Be("fresh");
            newGeneration.Should().Be(generation + 1, "a successful store must advance the generation, the same way Invalidate does");
        }

        [Fact]
        public void Invalidate_AfterASuccessfulStore_ClearsItAndAdvancesTheGenerationAgain()
        {
            var entry = new HueResourceCatalog.Entry();
            entry.TryStore(MakeSnapshot("v1"), 0);

            entry.Invalidate();

            var (snapshot, generation) = entry.ReadState();
            snapshot.Should().BeNull();
            generation.Should().Be(2);
        }

        [Fact]
        public async Task ConcurrentTryStoreAndInvalidate_NeverLoseAGenerationIncrement()
        {
            // A real, non-cooperative thread race (no TaskCompletionSource needed - the property
            // under test is the atomicity of the lock itself, not an interleaving of awaits).
            // Every successful TryStore and every Invalidate call increments the generation
            // exactly once, and nothing else does; if the shared lock ever let two of those
            // read-modify-write sequences interleave (the exact class of bug fixed here - the
            // prior implementation checked the generation and wrote the snapshot as two separate,
            // unsynchronized steps), some increments would be silently lost.
            var entry = new HueResourceCatalog.Entry();
            const int attempts = 300;
            var successCount = 0;

            var storeTasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(() =>
            {
                var (_, generation) = entry.ReadState();
                if (entry.TryStore(MakeSnapshot("x"), generation))
                {
                    Interlocked.Increment(ref successCount);
                }
            }));
            var invalidateTasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(() => entry.Invalidate()));

            await Task.WhenAll(storeTasks.Concat(invalidateTasks));

            var (_, finalGeneration) = entry.ReadState();
            finalGeneration.Should().Be(successCount + attempts,
                "every successful store and every Invalidate must advance the generation exactly once, with no lost updates under real concurrency");
        }
    }
}
