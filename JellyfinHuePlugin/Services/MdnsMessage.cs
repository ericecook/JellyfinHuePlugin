using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// The one mDNS query the plugin sends and the parser for what comes back. Pure: no
    /// sockets, no log. A Hue bridge answers a PTR query for _hue._tcp.local with a PTR to its
    /// service instance plus, in the additional section, that instance's SRV (port and host),
    /// TXT (bridgeid=…, modelid=…) and the host's A record. Anything malformed parses as
    /// "no bridges" rather than throwing.
    /// </summary>
    internal static class MdnsMessage
    {
        internal const string ServiceName = "_hue._tcp.local";

        private const ushort TypeA = 1;
        private const ushort TypePtr = 12;
        private const ushort TypeTxt = 16;
        private const ushort TypeSrv = 33;
        private const int HeaderLength = 12;
        private const int MaxPointerHops = 32;

        /// <summary>Id 0, no flags, one question: PTR for the service, class IN with the QU bit so the bridge may answer unicast.</summary>
        internal static byte[] BuildQuery()
        {
            var packet = new List<byte>(40) { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in ServiceName.Split('.'))
            {
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(label));
            }

            packet.Add(0);
            packet.AddRange(new byte[] { 0, (byte)TypePtr, 0x80, 1 });
            return packet.ToArray();
        }

        /// <summary>
        /// One entry per service instance that has a TXT bridgeid. The address is the A record
        /// of the instance's SRV target, else <paramref name="sender"/>; a port other than 443
        /// is appended as host:port. Never throws.
        /// </summary>
        internal static IReadOnlyList<HueBridgeDiscovery> ParseResponse(ReadOnlySpan<byte> packet, IPAddress sender)
        {
            try
            {
                return Parse(packet, sender);
            }
            catch (FormatException)
            {
                return Array.Empty<HueBridgeDiscovery>();
            }
        }

        private static IReadOnlyList<HueBridgeDiscovery> Parse(ReadOnlySpan<byte> packet, IPAddress sender)
        {
            if (packet.Length < HeaderLength || (ReadU16(packet, 2) & 0x8000) == 0)
            {
                return Array.Empty<HueBridgeDiscovery>();
            }

            var questions = ReadU16(packet, 4);
            var records = ReadU16(packet, 6) + ReadU16(packet, 8) + ReadU16(packet, 10);
            var offset = HeaderLength;
            for (var i = 0; i < questions; i++)
            {
                (_, offset) = ReadName(packet, offset);
                offset += 4;
            }

            var instances = new List<string>();
            var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < records; i++)
            {
                string name;
                (name, offset) = ReadName(packet, offset);
                Require(packet, offset, 10);
                var type = ReadU16(packet, offset);
                var rdataLength = ReadU16(packet, offset + 8);
                var rdata = offset + 10;
                Require(packet, rdata, rdataLength);

                switch (type)
                {
                    case TypePtr when string.Equals(name, ServiceName, StringComparison.OrdinalIgnoreCase):
                        instances.Add(ReadName(packet, rdata).Name);
                        break;
                    case TypeTxt:
                        txt[name] = ReadTxt(packet.Slice(rdata, rdataLength));
                        break;
                    case TypeSrv when rdataLength >= 7:
                        srv[name] = (ReadName(packet, rdata + 6).Name, ReadU16(packet, rdata + 4));
                        break;
                    case TypeA when rdataLength == 4:
                        addresses[name] = new IPAddress(packet.Slice(rdata, 4));
                        break;
                }

                offset = rdata + rdataLength;
            }

            var found = new List<HueBridgeDiscovery>();
            foreach (var instance in instances.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!txt.TryGetValue(instance, out var attributes) || !attributes.TryGetValue("bridgeid", out var bridgeId) || string.IsNullOrWhiteSpace(bridgeId))
                {
                    continue;
                }

                var address = sender;
                var port = 443;
                if (srv.TryGetValue(instance, out var service))
                {
                    port = service.Port;
                    if (addresses.TryGetValue(service.Target, out var resolved))
                    {
                        address = resolved;
                    }
                }

                found.Add(new HueBridgeDiscovery
                {
                    Id = bridgeId.Trim().ToLowerInvariant(),
                    InternalIpAddress = port == 443 ? address.ToString() : $"{address}:{port}"
                });
            }

            return found;
        }

        private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> rdata)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (offset < rdata.Length)
            {
                int length = rdata[offset];
                Require(rdata, offset + 1, length);
                var text = Encoding.ASCII.GetString(rdata.Slice(offset + 1, length));
                var equals = text.IndexOf('=');
                if (equals > 0)
                {
                    attributes[text[..equals]] = text[(equals + 1)..];
                }

                offset += 1 + length;
            }

            return attributes;
        }

        /// <summary>Reads a possibly-compressed name. Returns the offset just past the name as it appears at <paramref name="offset"/> (after the first pointer, when there is one).</summary>
        private static (string Name, int Next) ReadName(ReadOnlySpan<byte> packet, int offset)
        {
            var labels = new StringBuilder();
            var next = -1;
            var hops = 0;
            while (true)
            {
                Require(packet, offset, 1);
                int length = packet[offset];
                if (length == 0)
                {
                    offset++;
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    Require(packet, offset, 2);
                    var pointer = ((length & 0x3F) << 8) | packet[offset + 1];
                    if (next < 0)
                    {
                        next = offset + 2;
                    }

                    if (++hops > MaxPointerHops || pointer >= packet.Length)
                    {
                        throw new FormatException("compression pointer loop");
                    }

                    offset = pointer;
                    continue;
                }

                Require(packet, offset + 1, length);
                if (labels.Length > 0)
                {
                    labels.Append('.');
                }

                labels.Append(Encoding.ASCII.GetString(packet.Slice(offset + 1, length)));
                offset += 1 + length;
            }

            return (labels.ToString(), next < 0 ? offset : next);
        }

        private static ushort ReadU16(ReadOnlySpan<byte> packet, int offset)
        {
            Require(packet, offset, 2);
            return (ushort)((packet[offset] << 8) | packet[offset + 1]);
        }

        private static void Require(ReadOnlySpan<byte> packet, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > packet.Length)
            {
                throw new FormatException("truncated packet");
            }
        }
    }
}
