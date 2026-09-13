using System;
using System.Collections.Generic;
using System.Linq;

namespace JellyfinHuePlugin.Configuration
{
    /// <summary>
    /// What a configuration save did to the bridge list, matched by configuration id. A bridge
    /// whose address or application key differs is <c>Changed</c>: its certificate pin and
    /// caches describe the previous bridge. A missing id is <c>Removed</c>. Every other matched
    /// pair - identical or renamed - is <c>Unchanged</c>: the page carries its own copy of the
    /// bridge list and may post a stale pin back, so the caller restores the live one from these
    /// pairs. Added bridges are in none of the three. Pure; never throws on well-formed lists.
    /// </summary>
    public static class BridgeChanges
    {
        public sealed record Result(
            IReadOnlyList<(HueBridge Old, HueBridge New)> Changed,
            IReadOnlyList<HueBridge> Removed,
            IReadOnlyList<(HueBridge Old, HueBridge New)> Unchanged)
        {
            /// <summary>Nothing matched at all - only returned for an empty or null <c>before</c> list.</summary>
            public static readonly Result None = new(
                Array.Empty<(HueBridge, HueBridge)>(),
                Array.Empty<HueBridge>(),
                Array.Empty<(HueBridge, HueBridge)>());
        }

        public static Result Between(IReadOnlyList<HueBridge>? before, IReadOnlyList<HueBridge>? after)
        {
            if (before == null || before.Count == 0)
            {
                return Result.None;
            }

            var current = (after ?? Array.Empty<HueBridge>())
                .Where(b => b != null && !string.IsNullOrEmpty(b.Id))
                .GroupBy(b => b.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var changed = new List<(HueBridge, HueBridge)>();
            var removed = new List<HueBridge>();
            var unchanged = new List<(HueBridge, HueBridge)>();
            foreach (var old in before)
            {
                if (old == null || string.IsNullOrEmpty(old.Id))
                {
                    continue;
                }

                if (!current.TryGetValue(old.Id, out var updated))
                {
                    removed.Add(old);
                    continue;
                }

                if (!string.Equals(Normalize(old.IpAddress), Normalize(updated.IpAddress), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(old.Username ?? string.Empty, updated.Username ?? string.Empty, StringComparison.Ordinal))
                {
                    changed.Add((old, updated));
                }
                else
                {
                    unchanged.Add((old, updated));
                }
            }

            return new Result(changed, removed, unchanged);
        }

        private static string Normalize(string? address) => (address ?? string.Empty).Trim();
    }
}
