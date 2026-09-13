using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using JellyfinHuePlugin.Configuration;

namespace JellyfinHuePlugin.Managers
{
    /// <summary>Inputs to profile matching, already normalised (no nulls).</summary>
    internal readonly record struct MatchRequest(
        string ClientName,
        string DeviceId,
        string RemoteEndpoint,
        bool IsMovie,
        bool IsEpisode);

    internal enum MatchStatus { Matched, PluginDisabled, NoProfiles, NoMatch }

    internal enum RejectionFilter { Disabled, MediaType, IpAddress, DeviceId, ClientName }

    /// <summary>Why one profile was skipped. Actual and Expected are the compared values.</summary>
    internal sealed record Rejection(string ProfileName, RejectionFilter Filter, string Actual, string Expected);

    internal sealed record MatchResult(MatchStatus Status, LightControlProfile? Profile, IReadOnlyList<Rejection> Rejections)
    {
        internal static readonly MatchResult PluginDisabled = new(MatchStatus.PluginDisabled, null, Array.Empty<Rejection>());
        internal static readonly MatchResult NoProfiles = new(MatchStatus.NoProfiles, null, Array.Empty<Rejection>());
    }

    /// <summary>
    /// Pure profile matching: no logging, no Jellyfin types, no state. The manager
    /// builds a <see cref="MatchRequest"/> from the playback event and logs the result.
    /// Profiles are tried in list order; the first match wins.
    /// </summary>
    internal static class ProfileMatcher
    {
        internal static MatchResult FindMatchingProfile(PluginConfiguration config, MatchRequest request)
        {
            if (!config.EnablePlugin)
            {
                return MatchResult.PluginDisabled;
            }

            if (config.Profiles == null || config.Profiles.Count == 0)
            {
                return MatchResult.NoProfiles;
            }

            var rejections = new List<Rejection>();
            foreach (var profile in config.Profiles)
            {
                if (!profile.Enabled)
                {
                    rejections.Add(new Rejection(profile.Name, RejectionFilter.Disabled, "Enabled=false", "Enabled=true"));
                    continue;
                }

                if (!MediaTypeMatches(profile, request.IsMovie, request.IsEpisode))
                {
                    rejections.Add(new Rejection(profile.Name, RejectionFilter.MediaType,
                        DescribeMediaType(request.IsMovie, request.IsEpisode), DescribeEnabledTypes(profile)));
                    continue;
                }

                var rejection = FirstFailingFilter(profile, request);
                if (rejection != null)
                {
                    rejections.Add(rejection);
                    continue;
                }

                return new MatchResult(MatchStatus.Matched, profile, rejections);
            }

            return new MatchResult(MatchStatus.NoMatch, null, rejections);
        }

        internal static bool MediaTypeMatches(LightControlProfile profile, bool isMovie, bool isEpisode)
        {
            if (isMovie)
            {
                return profile.EnableForMovies;
            }

            if (isEpisode)
            {
                return profile.EnableForTvShows;
            }

            // Unknown media type never matches
            return false;
        }

        /// <summary>Returns null when the profile's client filters accept the request, otherwise the first failing filter.</summary>
        internal static Rejection? FirstFailingFilter(LightControlProfile profile, MatchRequest request)
        {
            bool hasClientFilter = !string.IsNullOrWhiteSpace(profile.TargetClientName);
            bool hasDeviceFilter = profile.TargetDeviceIds is { Count: > 0 };
            bool hasIpFilter = !string.IsNullOrWhiteSpace(profile.TargetIpAddress);

            // No filters: matches everything
            if (!hasClientFilter && !hasDeviceFilter && !hasIpFilter)
            {
                return null;
            }

            // IP address (most specific). Both sides go through ExtractIpAddress so a
            // target typed as 2001:DB8:0:0:0:0:0:1 matches a client at 2001:db8::1.
            if (hasIpFilter)
            {
                var clientIp = ExtractIpAddress(request.RemoteEndpoint);
                var targetIp = ExtractIpAddress(profile.TargetIpAddress);
                if (!clientIp.Equals(targetIp, StringComparison.OrdinalIgnoreCase))
                {
                    return new Rejection(profile.Name, RejectionFilter.IpAddress, clientIp, profile.TargetIpAddress);
                }
            }

            // Device ID list
            if (hasDeviceFilter)
            {
                bool deviceMatches = profile.TargetDeviceIds.Any(id => request.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (!deviceMatches)
                {
                    return new Rejection(profile.Name, RejectionFilter.DeviceId, request.DeviceId, string.Join(", ", profile.TargetDeviceIds));
                }
            }

            // Client name (least specific, substring match)
            if (hasClientFilter)
            {
                if (!request.ClientName.Contains(profile.TargetClientName, StringComparison.OrdinalIgnoreCase))
                {
                    return new Rejection(profile.Name, RejectionFilter.ClientName, request.ClientName, profile.TargetClientName);
                }
            }

            return null;
        }

        /// <summary>
        /// Canonical address of an endpoint string: bare IPv4, IPv4:port, bare IPv6 or
        /// [IPv6]:port. IPv4-mapped IPv6 is unwrapped. Anything that does not parse is
        /// returned trimmed and unchanged so a rejection can still show it.
        /// </summary>
        internal static string ExtractIpAddress(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return string.Empty;
            }

            var trimmed = endpoint.Trim();
            if (!IPEndPoint.TryParse(trimmed, out var parsed))
            {
                return trimmed;
            }

            var address = parsed.Address;
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            return address.ToString();
        }

        private static string DescribeMediaType(bool isMovie, bool isEpisode)
            => isMovie ? "Movie" : isEpisode ? "Episode" : "Unknown";

        private static string DescribeEnabledTypes(LightControlProfile profile)
        {
            var types = new List<string>(2);
            if (profile.EnableForMovies) types.Add("Movies");
            if (profile.EnableForTvShows) types.Add("TvShows");
            return types.Count == 0 ? "none" : string.Join(", ", types);
        }
    }
}
