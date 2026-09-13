using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// The one query the plugin sends and the answers it must understand. The real-bridge
    /// fixture is the byte-exact reply of a Hue Bridge Pro (BSB003) captured on 2026-09-13;
    /// the hand-built packets cover the shapes no bridge on hand produces.
    /// </summary>
    public class MdnsMessageTests
    {
        // Captured with one PTR query for _hue._tcp.local, QU bit set. Contains PTR, SRV, TXT,
        // A, two AAAA and one NSEC record; the A record names 192.168.1.170.
        private const string RealResponseHex =
            "000084000001000100000006045f687565045f746370056c6f63616c00000c8001c00c000c00010000000a0016" +
            "1348756520427269646765202d20433033413439c00cc02d002100010000000a00150000000001bb0c43343239" +
            "3936433033413439c016c02d001000010000000a00291962726964676569643d63343239393666666665633033" +
            "6134390e6d6f64656c69643d425342303033c055001c00010000000a0010fe80000000000000c62996fffeb03a" +
            "49c055000100010000000a0004c0a801aac055001c00010000000a00102605a601ac384f00c62996fffeb03a49" +
            "c055001c00010000000a00102605a601ac384f000000000000000003";

        private const string Instance = "Hue Bridge - C03A49._hue._tcp.local";
        private const string Host = "C42996C03A49.local";
        private static readonly IPAddress Sender = IPAddress.Parse("10.0.0.9");

        /// <summary>Builds an uncompressed DNS packet with every record in the answer section.</summary>
        private sealed class PacketBuilder
        {
            private readonly List<(string Name, ushort Type, byte[] Rdata)> _records = new();

            public static byte[] Name(string name)
            {
                var bytes = new List<byte>();
                foreach (var label in name.Split('.'))
                {
                    bytes.Add((byte)label.Length);
                    bytes.AddRange(Encoding.ASCII.GetBytes(label));
                }

                bytes.Add(0);
                return bytes.ToArray();
            }

            public PacketBuilder Ptr(string name, string target) => Add(name, 12, Name(target));

            public PacketBuilder Txt(string name, params string[] strings) =>
                Add(name, 16, strings.SelectMany(s => new[] { (byte)s.Length }.Concat(Encoding.ASCII.GetBytes(s))).ToArray());

            public PacketBuilder Srv(string name, ushort port, string target) =>
                Add(name, 33, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(Name(target)).ToArray());

            public PacketBuilder A(string name, string ip) => Add(name, 1, IPAddress.Parse(ip).GetAddressBytes());

            public PacketBuilder Raw(string name, ushort type, byte[] rdata) => Add(name, type, rdata);

            private PacketBuilder Add(string name, ushort type, byte[] rdata)
            {
                _records.Add((name, type, rdata));
                return this;
            }

            public byte[] Build(ushort flags = 0x8400)
            {
                var packet = new List<byte>
                {
                    0, 0, (byte)(flags >> 8), (byte)flags, 0, 0, (byte)(_records.Count >> 8), (byte)_records.Count, 0, 0, 0, 0
                };
                foreach (var (name, type, rdata) in _records)
                {
                    packet.AddRange(Name(name));
                    packet.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(rdata.Length >> 8), (byte)rdata.Length });
                    packet.AddRange(rdata);
                }

                return packet.ToArray();
            }
        }

        private static PacketBuilder Bridge(ushort port = 443, string? ip = "192.168.1.170", string bridgeIdTxt = "bridgeid=c42996fffec03a49")
        {
            var builder = new PacketBuilder()
                .Ptr(MdnsMessage.ServiceName, Instance)
                .Srv(Instance, port, Host)
                .Txt(Instance, bridgeIdTxt, "modelid=BSB003");
            if (ip != null)
            {
                builder.A(Host, ip);
            }

            return builder;
        }

        [Fact]
        public void BuildQuery_IsOnePtrQuestionWithTheQuBit()
        {
            var expected = new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }
                .Concat(PacketBuilder.Name("_hue._tcp.local"))
                .Concat(new byte[] { 0, 12, 0x80, 1 })
                .ToArray();

            MdnsMessage.BuildQuery().Should().Equal(expected);
        }

        [Fact]
        public void ParseResponse_RealBridgeAnswer_YieldsIdAndAddressFromTheARecord()
        {
            var packet = Convert.FromHexString(RealResponseHex);

            var found = MdnsMessage.ParseResponse(packet, Sender);

            var bridge = found.Should().ContainSingle().Subject;
            bridge.Id.Should().Be("c42996fffec03a49");
            bridge.InternalIpAddress.Should().Be("192.168.1.170", "the A record wins over the sender address");
        }

        [Fact]
        public void ParseResponse_WithoutAnARecord_UsesTheSender()
        {
            var found = MdnsMessage.ParseResponse(Bridge(ip: null).Build(), Sender);

            found.Should().ContainSingle().Which.InternalIpAddress.Should().Be("10.0.0.9");
        }

        [Fact]
        public void ParseResponse_NonStandardPort_IsAppendedToTheAddress()
        {
            var found = MdnsMessage.ParseResponse(Bridge(port: 8443).Build(), Sender);

            found.Should().ContainSingle().Which.InternalIpAddress.Should().Be("192.168.1.170:8443");
        }

        [Fact]
        public void ParseResponse_UpperCaseBridgeId_IsLowered()
        {
            var found = MdnsMessage.ParseResponse(Bridge(bridgeIdTxt: "bridgeid=C42996FFFEC03A49").Build(), Sender);

            found.Should().ContainSingle().Which.Id.Should().Be("c42996fffec03a49");
        }

        [Fact]
        public void ParseResponse_QueryPacket_IsIgnored()
        {
            MdnsMessage.ParseResponse(Bridge().Build(flags: 0), Sender).Should().BeEmpty();
        }

        [Fact]
        public void ParseResponse_TxtWithoutBridgeId_IsIgnored()
        {
            MdnsMessage.ParseResponse(Bridge(bridgeIdTxt: "foo=bar").Build(), Sender).Should().BeEmpty();
        }

        [Fact]
        public void ParseResponse_UnknownRecordTypesBeforeTheUsefulOnes_AreSkipped()
        {
            var packet = new PacketBuilder()
                .Raw(Instance, 47, new byte[] { 0xc0, 0x27, 0, 5, 0, 0, 0x80, 0, 0x40 })
                .Raw(Host, 28, new byte[16])
                .Ptr(MdnsMessage.ServiceName, Instance)
                .Srv(Instance, 443, Host)
                .Txt(Instance, "bridgeid=c42996fffec03a49")
                .A(Host, "192.168.1.170")
                .Build();

            var found = MdnsMessage.ParseResponse(packet, Sender);

            found.Should().ContainSingle().Which.InternalIpAddress.Should().Be("192.168.1.170");
        }

        [Fact]
        public void ParseResponse_TwoBridges_YieldsBoth()
        {
            var packet = new PacketBuilder()
                .Ptr(MdnsMessage.ServiceName, "One._hue._tcp.local")
                .Ptr(MdnsMessage.ServiceName, "Two._hue._tcp.local")
                .Txt("One._hue._tcp.local", "bridgeid=001788fffe000001")
                .Txt("Two._hue._tcp.local", "bridgeid=001788fffe000002")
                .Srv("One._hue._tcp.local", 443, "one.local")
                .Srv("Two._hue._tcp.local", 443, "two.local")
                .A("one.local", "192.168.1.1")
                .A("two.local", "192.168.1.2")
                .Build();

            var found = MdnsMessage.ParseResponse(packet, Sender);

            found.Select(b => (b.Id, b.InternalIpAddress)).Should().BeEquivalentTo(new[]
            {
                ("001788fffe000001", "192.168.1.1"),
                ("001788fffe000002", "192.168.1.2")
            });
        }

        [Fact]
        public void ParseResponse_TruncatedPacket_ReturnsEmptyWithoutThrowing()
        {
            var packet = Convert.FromHexString(RealResponseHex).AsSpan(0, 100);

            // Span<byte> is a ref struct and cannot be captured by a lambda/Func<> (CS8175), so
            // this calls ParseResponse directly instead of going through Should().NotThrow(): an
            // exception here still fails the test, and the result is asserted empty either way.
            MdnsMessage.ParseResponse(packet, Sender).Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        public void ParseResponse_ShorterThanAHeader_ReturnsEmpty(int length)
        {
            MdnsMessage.ParseResponse(new byte[length], Sender).Should().BeEmpty();
        }

        [Fact]
        public void ParseResponse_CompressionPointerLoop_ReturnsEmptyWithoutThrowing()
        {
            // Header says one answer; its name is a pointer to offset 12, i.e. to itself.
            var packet = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xc0, 0x0c, 0, 12, 0x80, 1, 0, 0, 0, 10, 0, 2, 0xc0, 0x0c };

            var act = () => MdnsMessage.ParseResponse(packet, Sender);

            act.Should().NotThrow().Which.Should().BeEmpty();
        }
    }
}
