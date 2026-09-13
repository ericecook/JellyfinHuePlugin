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
        private sealed record Snapshot(IReadOnlyList<HueGroupResource> Groups, IReadOnlyList<HueSceneResource> Scenes);

        private sealed class Entry
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public volatile Snapshot? Snapshot;

            // Bumped every time Snapshot is replaced (by a completed load) or cleared (by
            // Invalidate). Lets a concurrent LoadAsync detect, without taking the gate, that its
            // in-hand result is stale (Race 1) or that a refresh it's about to perform has
            // already happened (Race 2) - see LoadAsync and Invalidate.
            public int Generation;
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
            if (_entries.TryGetValue(bridge.Id, out var entry))
            {
                // Never takes Gate: a caller that just wants to drop the cache must not block on
                // a slow bridge call. Clear the snapshot before bumping the generation, so that
                // any thread which observes the new generation is guaranteed to also observe the
                // cleared snapshot (both are strong-fence writes; program order between them is
                // preserved for all observers).
                entry.Snapshot = null;
                Interlocked.Increment(ref entry.Generation);
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
            if (!refresh && entry.Snapshot is { } cached)
            {
                return cached;
            }

            var generationAtEntry = Volatile.Read(ref entry.Generation);
            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                if (!refresh && entry.Snapshot is { } loadedMeanwhile)
                {
                    return loadedMeanwhile;
                }

                // Another caller already refreshed (or invalidated) since we decided we needed a
                // refresh, while we were queued for the gate: use whatever is current instead of
                // hitting the bridge again. Keeps "one refresh on a miss" true under concurrency.
                if (refresh && Volatile.Read(ref entry.Generation) != generationAtEntry)
                {
                    return entry.Snapshot;
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

                var snapshot = new Snapshot(groups, enriched);

                // An Invalidate landed while the bridge calls above were in flight: this result
                // is stale by definition. Discard it instead of writing it back, which would
                // silently undo the invalidation.
                if (Volatile.Read(ref entry.Generation) != generationAtEntry)
                {
                    return entry.Snapshot;
                }

                entry.Snapshot = snapshot;
                Interlocked.Increment(ref entry.Generation);
                return snapshot;
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);
    }
}
