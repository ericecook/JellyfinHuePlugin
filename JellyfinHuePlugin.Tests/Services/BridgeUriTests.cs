using FluentAssertions;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    public class BridgeUriTests
    {
        [Theory]
        [InlineData("192.168.1.100", "192.168.1.100")]
        [InlineData("http://192.168.1.100", "192.168.1.100")]
        [InlineData("https://192.168.1.100", "192.168.1.100")]
        [InlineData("192.168.1.100/", "192.168.1.100")]
        [InlineData("https://192.168.1.100/api/config", "192.168.1.100")]
        [InlineData("192.168.1.100:8080", "192.168.1.100:8080")]
        [InlineData("bridge.local", "bridge.local")]
        [InlineData("bridge.local:8080", "bridge.local:8080")]
        [InlineData("host.docker.internal:9443", "host.docker.internal:9443")]
        [InlineData("", "")]
        public void StripSchemeAndPath_RemovesSchemeAndPathOnly(string input, string expected)
        {
            BridgeUri.StripSchemeAndPath(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("192.168.1.50", "192.168.1.50")]
        [InlineData("192.168.1.50:8443", "192.168.1.50:8443")]
        [InlineData("bridge.local", "bridge.local")]
        [InlineData("bridge.local:8080", "bridge.local:8080")]
        [InlineData("[fe80::1]", "[fe80::1]")]
        [InlineData("[fe80::1]:443", "[fe80::1]")]                 // 443 is the default https port, so Uri drops it
        [InlineData("https://192.168.1.50/api/", "192.168.1.50")]
        [InlineData("http://bridge.local:8080/x", "bridge.local:8080")]
        [InlineData("  192.168.1.50  ", "192.168.1.50")]
        public void TryParseHost_AcceptsHostsWithOptionalPort(string input, string expectedAuthority)
        {
            BridgeUri.TryParseHost(input, out var authority).Should().BeTrue();
            authority.Should().Be(expectedAuthority);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("user@192.168.1.50")]
        [InlineData("bridge.local?x=1")]
        [InlineData("bridge.local#frag")]
        [InlineData("bridge local")]
        [InlineData("http://")]
        [InlineData("192.168.1.50:notaport")]
        public void TryParseHost_RejectsEverythingElse(string input)
        {
            BridgeUri.TryParseHost(input, out _).Should().BeFalse();
        }

        [Fact]
        public void TryParseHost_DropsPathTraversalWithTheRestOfThePath()
        {
            BridgeUri.TryParseHost("192.168.1.50/../etc", out var authority).Should().BeTrue();
            authority.Should().Be("192.168.1.50");
        }

        [Theory]
        [InlineData("3883f8bf-30a3-445b-ac06-b047d50599df")]
        [InlineData("Qn74cB7YlKursSzMYyPL4pr5oLWxayBqhKyjFD10")]
        [InlineData("1")]
        [InlineData("abc-DEF_1.2")]
        public void IsValidSegment_AcceptsKeysAndIds(string value)
        {
            BridgeUri.IsValidSegment(value).Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("a/b")]
        [InlineData("a b")]
        [InlineData("a?b")]
        [InlineData("../x")]
        [InlineData("key%2F")]
        public void IsValidSegment_RejectsAnythingThatCouldChangeThePath(string? value)
        {
            BridgeUri.IsValidSegment(value).Should().BeFalse();
        }

        [Fact]
        public void Build_JoinsSegmentsUnderHttps()
        {
            BridgeUri.Build("192.168.1.50:8443", "clip", "v2", "resource", "grouped_light", "abc")
                .ToString().Should().Be("https://192.168.1.50:8443/clip/v2/resource/grouped_light/abc");
        }
    }
}
