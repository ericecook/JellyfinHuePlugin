using System;
using System.Linq;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// Pure helpers for bridge addresses and request URIs. Nothing here touches the network
    /// or the log; callers decide what to do with a false.
    /// </summary>
    internal static class BridgeUri
    {
        /// <summary>Strips http:// or https:// and anything from the first slash. Whitespace-only input is returned as is.</summary>
        internal static string StripSchemeAndPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input ?? string.Empty;
            }

            var value = input.Trim();
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(8);
            }
            else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(7);
            }

            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value.Substring(0, slash);
            }

            return value;
        }

        /// <summary>
        /// True when the input, after stripping, is an IPv4 literal, a bracketed IPv6 literal
        /// or a DNS name, with an optional port and nothing else: no user info, path, query or
        /// fragment. The output is the authority as Uri renders it (brackets and port kept).
        /// </summary>
        internal static bool TryParseHost(string? input, out string authority)
        {
            authority = string.Empty;
            var stripped = StripSchemeAndPath(input);
            if (stripped.Length == 0 || stripped.Any(char.IsWhiteSpace))
            {
                return false;
            }

            if (!Uri.TryCreate("https://" + stripped, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var hostType = uri.HostNameType;
            if (hostType != UriHostNameType.IPv4 && hostType != UriHostNameType.IPv6 && hostType != UriHostNameType.Dns)
            {
                return false;
            }

            if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                return false;
            }

            authority = uri.Authority;
            return true;
        }

        /// <summary>Letters, digits, dot, dash and underscore; non-empty; not all dots, since Uri would collapse a dot-segment. Covers v2 UUIDs, v1 keys and v1 ids.</summary>
        internal static bool IsValidSegment(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                {
                    return false;
                }
            }

            if (value.All(c => c == '.'))
            {
                return false;
            }

            return true;
        }

        /// <summary>https://{authority}/{segments joined by '/'}. Segments must already be valid.</summary>
        internal static Uri Build(string authority, params string[] segments)
        {
            return new Uri("https://" + authority + "/" + string.Join('/', segments), UriKind.Absolute);
        }
    }
}
