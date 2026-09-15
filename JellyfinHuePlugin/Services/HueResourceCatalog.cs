using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    /// <summary>A bridge's rooms and zones, and its scenes with GroupName filled, as the plugin page lists them.</summary>
    public sealed record HueTargets(IReadOnlyList<HueGroupResource> Groups, IReadOnlyList<HueSceneResource> Scenes);

    /// <summary>
    /// Resolves the ids a profile stores: a v2 UUID is used as is, and "0" (or empty) means the
    /// bridge home, whose grouped_light id is cached per bridge; nothing else resolves. Also reads
    /// a bridge's rooms, zones and scenes for the plugin page, uncached. Nothing here writes
    /// configuration.
    /// </summary>
    public class HueResourceCatalog
    {
        /// <summary>The group types the plugin page lists.</summary>
        internal static readonly string[] RoomAndZone = { "room", "zone" };

        /// <summary>The group type "0" resolves to.</summary>
        internal static readonly string[] BridgeHome = { "bridge_home" };

        // Internal rather than private so the (Home, Generation) synchronization contract - the
        // exact mechanism that keeps Invalidate from racing a write - can be unit-tested
        // directly, the same way HueService exposes ShouldAcceptBridgeCertificate for its own
        // narrow, otherwise-untestable concurrency/security seam.
        internal sealed class Entry
        {
            // Serializes the actual bridge fetch (at most one in flight per bridge).
            public readonly SemaphoreSlim Gate = new(1, 1);

            // Guards (Home, Generation) as a single unit. Invalidate touches neither Gate nor
            // an await, so only a plain lock - held briefly, never across an await - can make
            // "check the generation, then write the home" atomic against a concurrent
            // Invalidate. Generation increments on every state change (a store and a clear both
            // count), which is what lets TryStore tell a home read before an Invalidate landed
            // from one read after it.
            private readonly object _stateLock = new();
            private string? _home;
            private int _generation;

            public (string? Home, int Generation) ReadState()
            {
                lock (_stateLock)
                {
                    return (_home, _generation);
                }
            }

            public void Invalidate()
            {
                lock (_stateLock)
                {
                    _home = null;
                    _generation++;
                }
            }

            /// <summary>Stores <paramref name="home"/> unless the generation has moved past
            /// <paramref name="expectedGeneration"/> - meaning an Invalidate, or another caller's
            /// store, happened since this home was read - in which case nothing is written.
            /// </summary>
            public bool TryStore(string home, int expectedGeneration)
            {
                lock (_stateLock)
                {
                    if (_generation != expectedGeneration)
                    {
                        return false;
                    }

                    _home = home;
                    _generation++;
                    return true;
                }
            }
        }

        private readonly HueService _hueService;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Entry> _entries = new();

        public HueResourceCatalog(HueService hueService, ILogger<HueResourceCatalog> logger)
        {
            _hueService = hueService;
            _logger = logger;
        }

        // What a load produced, and - when it produced nothing - why. Three cases, not two:
        // the bridge fetch itself failed; the fetch succeeded but a concurrent Invalidate
        // discarded the result before it could be stored; or (the residual case, once those two
        // are ruled out) the target genuinely is not on the bridge. Each needs different advice,
        // so none of the three may collapse into either of the others.
        private readonly record struct LoadResult(string? Home, bool FetchFailed, bool Invalidated);

        /// <summary>
        /// The bridge's rooms and zones, then its scenes with GroupName filled, read from the bridge
        /// on every call; nothing is cached. Null when either read fails.
        /// </summary>
        public virtual async Task<HueTargets?> ReadTargetsAsync(HueBridge bridge, CancellationToken cancellationToken)
        {
            var groups = await _hueService.GetGroupsAsync(bridge, RoomAndZone, cancellationToken);
            if (groups == null)
            {
                return null;
            }

            var scenes = await _hueService.GetScenesAsync(bridge, cancellationToken);
            if (scenes == null)
            {
                return null;
            }

            var groupsById = groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
            var enriched = scenes
                .Select(s => groupsById.TryGetValue(s.GroupId, out var g) ? s with { GroupName = g.Name } : s)
                .ToList();

            return new HueTargets(groups, enriched);
        }

        /// <summary>
        /// A UUID → itself, without reading the bridge; "0" (or empty) → the bridge home's grouped
        /// light. Null, with one log line, for any other value (such as a group number stored before
        /// 4.0) or when the bridge home cannot be read.
        /// </summary>
        public virtual async Task<string?> ResolveGroupedLightAsync(HueBridge bridge, string targetGroupId, CancellationToken cancellationToken)
        {
            if (IsUuid(targetGroupId))
            {
                return targetGroupId;
            }

            if (!string.IsNullOrWhiteSpace(targetGroupId) && targetGroupId != "0")
            {
                LogUnresolved("group", targetGroupId, bridge, fetchFailed: false, invalidated: false);
                return null;
            }

            var load = await LoadHomeAsync(bridge, cancellationToken);
            if (load.Home == null)
            {
                LogUnresolved("group", targetGroupId, bridge, load.FetchFailed, load.Invalidated);
            }

            return load.Home;
        }

        /// <summary>A UUID → itself, without reading the bridge. Null, with one warning, for any other value (such as a scene id stored before 4.0).</summary>
        public virtual Task<string?> ResolveSceneAsync(HueBridge bridge, string sceneId, CancellationToken cancellationToken)
        {
            if (IsUuid(sceneId))
            {
                return Task.FromResult<string?>(sceneId);
            }

            LogUnresolved("scene", sceneId, bridge, fetchFailed: false, invalidated: false);
            return Task.FromResult<string?>(null);
        }

        /// <summary>Drops the cached bridge home for the bridge; the next resolve of "0" reads it again.</summary>
        public virtual void Invalidate(HueBridge bridge)
        {
            // Never takes Gate: a caller that just wants to drop the cache must not block on a
            // slow bridge call. The short, await-free lock inside Entry.Invalidate is enough to
            // make this atomic with LoadHomeAsync's own state reads and writes.
            //
            // Must compose the key exactly as the load path does, or invalidation silently stops
            // finding anything: hence the shared EntryKey.
            if (_entries.TryGetValue(EntryKey(bridge), out var entry))
            {
                entry.Invalidate();
            }
        }

        /// <summary>
        /// The cache key for a bridge's entry. The configuration id alone is not the identity the
        /// cached bridge home depends on - editing a bridge's address (or its application key)
        /// keeps the id but points it at a different bridge entirely - so the address and key are
        /// part of the key too. Repointing a bridge is then a plain cache miss, and serving the
        /// previous bridge's home stops being possible rather than merely needing an
        /// invalidation someone has to remember. Each component is length-prefixed, so no two
        /// different bridges can compose one key whatever their values happen to contain.
        /// </summary>
        private static string EntryKey(HueBridge bridge) =>
            $"{bridge.Id.Length}:{bridge.Id}|{bridge.IpAddress.Length}:{bridge.IpAddress}|{bridge.Username.Length}:{bridge.Username}";

        /// <summary>A target that could not be resolved, told apart from a bridge that could not be
        /// asked and from a resolve that lost a race with a concurrent Invalidate: only the
        /// genuinely-missing case is something the user can fix by re-selecting on the plugin
        /// page.</summary>
        private void LogUnresolved(string field, string value, HueBridge bridge, bool fetchFailed, bool invalidated)
        {
            if (fetchFailed)
            {
                _logger.LogWarning("Could not reach bridge {BridgeName} to resolve profile target {Field} '{Value}'; the bridge may be offline or its API key no longer valid",
                    bridge.Name, field, value);
                return;
            }

            if (invalidated)
            {
                // Self-healing: the cache was cleared mid-read, so the very next resolve
                // starts from a clean cache and tries again. Nothing is wrong with the target,
                // so this is not a warning.
                _logger.LogDebug("Cache for bridge {BridgeName} was invalidated while resolving profile target {Field} '{Value}'; the next resolve will retry",
                    bridge.Name, field, value);
                return;
            }

            _logger.LogWarning("Profile target {Field} '{Value}' not found on bridge {BridgeName}; re-select it on the plugin page",
                field, value, bridge.Name);
        }

        private async Task<LoadResult> LoadHomeAsync(HueBridge bridge, CancellationToken cancellationToken)
        {
            var entry = _entries.GetOrAdd(EntryKey(bridge), _ => new Entry());
            if (entry.ReadState().Home is { } cached)
            {
                return new LoadResult(cached, false, false);
            }

            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                var (home, generation) = entry.ReadState();

                // Stored by the caller that held the gate while this one waited: concurrent
                // resolves of "0" share one bridge read.
                if (home != null)
                {
                    return new LoadResult(home, false, false);
                }

                var groups = await _hueService.GetGroupsAsync(bridge, BridgeHome, cancellationToken);
                if (groups == null)
                {
                    return new LoadResult(null, true, false);
                }

                // No bridge home with a grouped_light service: not cached, so the next resolve reads again.
                var fresh = groups.FirstOrDefault(g => g.Type == "bridge_home")?.GroupedLightId;
                if (fresh == null)
                {
                    return new LoadResult(null, false, false);
                }

                if (entry.TryStore(fresh, generation))
                {
                    return new LoadResult(fresh, false, false);
                }

                // An Invalidate landed while the bridge call above was in flight (the gate we
                // are still holding rules out any other cause of TryStore's refusal), so this
                // fetch's result is stale by definition and was deliberately discarded - the
                // home Invalidate leaves behind is always null. This is neither a bridge
                // failure nor a missing target: the next resolve sees the cleared cache and
                // retries on its own.
                return new LoadResult(entry.ReadState().Home, false, true);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);
    }
}
