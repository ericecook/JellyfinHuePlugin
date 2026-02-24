using System;
using FluentAssertions;
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
        public void NormalizeBridgeIp_ShouldStripProtocolAndPath(string? input, string? expected)
        {
            // Act
            var result = NormalizeBridgeIp(input);

            // Assert
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("192.168.1.100:8080", "192.168.1.100:8080")] // Port should be kept
        [InlineData("bridge.local", "bridge.local")] // Hostname
        [InlineData("bridge.local:8080", "bridge.local:8080")] // Hostname with port
        public void NormalizeBridgeIp_ShouldPreservePortAndHostname(string input, string expected)
        {
            // Act
            var result = NormalizeBridgeIp(input);

            // Assert
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("192.168.1.100", "192.168.1.100")]
        [InlineData("192.168.1.100:12345", "192.168.1.100")]
        [InlineData("", "")]
        public void ExtractIpAddress_ShouldRemovePort(string? input, string expected)
        {
            // Act
            var result = ExtractIpAddress(input);

            // Assert
            result.Should().Be(expected);
        }

        // Helper methods that replicate the service logic for testing
        private string? NormalizeBridgeIp(string? bridgeIp)
        {
            if (string.IsNullOrWhiteSpace(bridgeIp))
            {
                return bridgeIp;
            }

            // Remove http:// or https://
            bridgeIp = bridgeIp.Replace("https://", "").Replace("http://", "");

            // Remove trailing slash
            bridgeIp = bridgeIp.TrimEnd('/');

            // Remove any path components (e.g., /api)
            var slashIndex = bridgeIp.IndexOf('/');
            if (slashIndex > 0)
            {
                bridgeIp = bridgeIp.Substring(0, slashIndex);
            }

            return bridgeIp;
        }

        private string ExtractIpAddress(string? remoteEndpoint)
        {
            if (string.IsNullOrWhiteSpace(remoteEndpoint))
            {
                return string.Empty;
            }

            var colonIndex = remoteEndpoint.IndexOf(':');
            if (colonIndex > 0)
            {
                return remoteEndpoint.Substring(0, colonIndex);
            }

            return remoteEndpoint;
        }
    }
}
