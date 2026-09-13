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
    /// <summary>
    /// Rooms, zones, the bridge home and scenes of each bridge, fetched on first use and
    /// cached until a miss or Invalidate. Resolves the ids a profile stores: a v2 UUID is
    /// used as is; "0" means the bridge home; a v1 number or scene id is looked up through
    /// id_v1. Nothing here writes configuration.
    /// </summary>
    public class HueResourceCatalog
    {
        // Internal rather than private so the (Snapshot, Generation) synchronization contract -
        // the exact mechanism that keeps Invalidate from racing a write - can be unit-tested
        // directly, the same way HueService exposes ShouldAcceptBridgeCertificate for its own
        // narrow, otherwise-untestable concurrency/security seam.
        internal sealed record Snapshot(IReadOnlyList<HueGroupResource> Groups, IReadOnlyList<HueSceneResource> Scenes);

        internal sealed class Entry
        {
            // Serializes the actual bridge fetch (at most one in flight per bridge).
            public readonly SemaphoreSlim Gate = new(1, 1);

            // Guards (Snapshot, Generation) as a single unit. Invalidate touches neither Gate nor
            // an await, so only a plain lock - held briefly, never across an await - can make
            // "check the generation, then write the snapshot" atomic against a concurrent
            // Invalidate. Generation increments on every state change (a store and a clear both
            // count), which is what lets LoadAsync tell "a peer already refreshed" (snapshot
            // present, generation moved) apart from "invalidated" (snapshot absent) after
            // reacquiring the gate.
            private readonly object _stateLock = new();
            private Snapshot? _snapshot;
            private int _generation;

            public (Snapshot? Snapshot, int Generation) ReadState()
            {
                lock (_stateLock)
                {
                    return (_snapshot, _generation);
                }
            }

            public void Invalidate()
            {
                lock (_stateLock)
                {
                    _snapshot = null;
                    _generation++;
                }
            }

            /// <summary>Stores <paramref name="snapshot"/> unless the generation has moved past
            /// <paramref name="expectedGeneration"/> - meaning an Invalidate, or another caller's
            /// store, happened since this snapshot was built - in which case nothing is written.
            /// </summary>
            public bool TryStore(Snapshot snapshot, int expectedGeneration)
            {
                lock (_stateLock)
                {
                    if (_generation != expectedGeneration)
                    {
                        return false;
                    }

                    _snapshot = snapshot;
                    _generation++;
                    return true;
                }
            }
        }

        private readonly HueService _hueService;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Entry> _entries = new();

        public HueResourceCatalog(HueService hueService, ILogger logger)
        {
            _hueService = hueService;
            _logger = logger;
        }

        public virtual async Task<IReadOnlyList<HueGroupResource>?> GetGroupsAsync(HueBridge bridge, CancellationToken cancellationToken)
            => (await LoadAsync(bridge, refresh: false, cancellationToken))?.Groups;

        public virtual async Task<IReadOnlyList<HueSceneResource>?> GetScenesAsync(HueBridge bridge, CancellationToken cancellationToken)
            => (await LoadAsync(bridge, refresh: false, cancellationToken))?.Scenes;

        /// <summary>
        /// "0" (or empty) → the bridge home's grouped light; a UUID → itself; a v1 number N → the
        /// room or zone whose id_v1 is "/groups/N". Null, after one refresh and one warning,
        /// when nothing matches.
        /// </summary>
        public virtual async Task<string?> ResolveGroupedLightAsync(HueBridge bridge, string targetGroupId, CancellationToken cancellationToken)
        {
            if (IsUuid(targetGroupId))
            {
                return targetGroupId;
            }

            var resolved = await ResolveAsync(bridge, snapshot => FindGroupedLight(snapshot, targetGroupId), cancellationToken);
            if (resolved == null)
            {
                _logger.LogWarning("Profile target {Field} '{Value}' not found on bridge {BridgeName}; re-select it on the plugin page",
                    "group", targetGroupId, bridge.Name);
            }

            return resolved;
        }

        /// <summary>A UUID → itself; otherwise the scene whose id_v1 is "/scenes/{id}". Null after one refresh and one warning.</summary>
        public virtual async Task<string?> ResolveSceneAsync(HueBridge bridge, string sceneId, CancellationToken cancellationToken)
        {
            if (IsUuid(sceneId))
            {
                return sceneId;
            }

            var resolved = await ResolveAsync(bridge, snapshot => snapshot.Scenes.FirstOrDefault(s => s.IdV1 == "/scenes/" + sceneId)?.Id, cancellationToken);
            if (resolved == null)
            {
                _logger.LogWarning("Profile target {Field} '{Value}' not found on bridge {BridgeName}; re-select it on the plugin page",
                    "scene", sceneId, bridge.Name);
            }

            return resolved;
        }

        /// <summary>Drops the cached resources for the bridge; the next call fetches again.</summary>
        public void Invalidate(HueBridge bridge)
        {
            // Never takes Gate: a caller that just wants to drop the cache must not block on a
            // slow bridge call. The short, await-free lock inside Entry.Invalidate is enough to
            // make this atomic with LoadAsync's own state reads and writes.
            if (_entries.TryGetValue(bridge.Id, out var entry))
            {
                entry.Invalidate();
            }
        }

        private static string? FindGroupedLight(Snapshot snapshot, string targetGroupId)
        {
            if (string.IsNullOrWhiteSpace(targetGroupId) || targetGroupId == "0")
            {
                return snapshot.Groups.FirstOrDefault(g => g.Type == "bridge_home")?.GroupedLightId;
            }

            return snapshot.Groups.FirstOrDefault(g => g.IdV1 == "/groups/" + targetGroupId)?.GroupedLightId;
        }

        private async Task<string?> ResolveAsync(HueBridge bridge, Func<Snapshot, string?> find, CancellationToken cancellationToken)
        {
            var snapshot = await LoadAsync(bridge, refresh: false, cancellationToken);
            if (snapshot == null)
            {
                return null;
            }

            var found = find(snapshot);
            if (found != null)
            {
                return found;
            }

            // The bridge may have changed since the cache was built: refresh once.
            snapshot = await LoadAsync(bridge, refresh: true, cancellationToken);
            return snapshot == null ? null : find(snapshot);
        }

        private async Task<Snapshot?> LoadAsync(HueBridge bridge, bool refresh, CancellationToken cancellationToken)
        {
            var entry = _entries.GetOrAdd(bridge.Id, _ => new Entry());
            var (snapshot, generation) = entry.ReadState();
            if (!refresh && snapshot is { } cached)
            {
                return cached;
            }

            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                var (currentSnapshot, currentGeneration) = entry.ReadState();

                // A peer changed the state while we were queued for the gate, and there is data:
                // it must have been a refresh (an Invalidate always leaves the snapshot null), so
                // use it instead of hitting the bridge again. Keeps "one refresh on a miss" true
                // under concurrency.
                if (currentSnapshot is { } peerRefreshed && currentGeneration != generation)
                {
                    return peerRefreshed;
                }

                if (!refresh && currentSnapshot is { } loadedMeanwhile)
                {
                    return loadedMeanwhile;
                }

                var groups = await _hueService.GetGroupsAsync(bridge, cancellationToken);
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
                    .Select(s => groupsById.TryGetValue(s.GroupId, out var g) ? s with { GroupName = g.Name, GroupedLightId = g.GroupedLightId } : s)
                    .ToList();

                var freshSnapshot = new Snapshot(groups, enriched);

                if (entry.TryStore(freshSnapshot, currentGeneration))
                {
                    return freshSnapshot;
                }

                // An Invalidate landed while the bridge calls above were in flight: this result is
                // stale by definition. Report whatever is current instead of serving data that was
                // explicitly discarded.
                return entry.ReadState().Snapshot;
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);
    }
}
