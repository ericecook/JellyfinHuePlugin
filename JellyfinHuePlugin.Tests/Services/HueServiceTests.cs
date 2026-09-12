using FluentAssertions;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    public class HueServiceTests
    {
        [Theory]
        [InlineData("192.168.1.100", "192.168.1.100")]
        [InlineData("http://192.168.1.100", "192.168.1.100")]
        [InlineData("https://192.168.1.100", "192.168.1.100")]
        [InlineData("192.168.1.100/", "192.168.1.100")]
        [InlineData("http://192.168.1.100/", "192.168.1.100")]
        [InlineData("https://192.168.1.100/", "192.168.1.100")]
        [InlineData("192.168.1.100/api", "192.168.1.100")]
        [InlineData("http://192.168.1.100/api", "192.168.1.100")]
        [InlineData("https://192.168.1.100/api/config", "192.168.1.100")]
        [InlineData("", "")]
        public void NormalizeBridgeIp_ShouldStripProtocolAndPath(string input, string expected)
        {
            HueService.NormalizeBridgeIp(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("192.168.1.100:8080", "192.168.1.100:8080")] // Port should be kept
        [InlineData("bridge.local", "bridge.local")] // Hostname
        [InlineData("bridge.local:8080", "bridge.local:8080")] // Hostname with port
        [InlineData("host.docker.internal:9443", "host.docker.internal:9443")]
        public void NormalizeBridgeIp_ShouldPreservePortAndHostname(string input, string expected)
        {
            HueService.NormalizeBridgeIp(input).Should().Be(expected);
        }
    }
}
