using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    public class PlaybackSessionManagerProfileTests
    {
        #region Basic Profile Matching Tests

        [Fact]
        public void ProfileMatching_NoProfilesConfigured_ShouldReturnNull()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>()
            };

            // Act
            var profile = FindMatchingProfile(config, "Roku", "device1", "192.168.1.100");

            // Assert
            profile.Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_PluginDisabled_ShouldReturnNull()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = false,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Test Profile",
                        TargetClientName = "Roku"
                    }
                }
            };

            // Act
            var profile = FindMatchingProfile(config, "Roku", "device1", "192.168.1.100");

            // Assert
            profile.Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_NoFiltersInProfile_ShouldMatchAll()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Match All",
                        TargetClientName = string.Empty,
                        TargetDeviceIds = new List<string>(),
                        TargetIpAddress = string.Empty
                    }
                }
            };

            // Act & Assert - Should match everything
            FindMatchingProfile(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Web", "device2", "192.168.1.101").Should().NotBeNull();
            FindMatchingProfile(config, "Android", "device3", "192.168.1.102").Should().NotBeNull();
        }

        #endregion

        #region Client Name Filtering Tests

        [Theory]
        [InlineData("Roku", "Roku-Living-Room", true)]
        [InlineData("Roku", "FireTV-Bedroom", false)]
        [InlineData("Web", "Web-Browser", true)]  // "Web-Browser" contains "Web"
        [InlineData("Web", "Roku", false)]
        public void ProfileMatching_ClientNameOnly_ShouldMatchSubstring(
            string filterName, string actualName, bool shouldMatch)
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Client Filter",
                        TargetClientName = filterName
                    }
                }
            };

            // Act
            var profile = FindMatchingProfile(config, actualName, "deviceId", "192.168.1.100");

            // Assert
            if (shouldMatch)
                profile.Should().NotBeNull();
            else
                profile.Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_ClientName_ShouldBeCaseInsensitive()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Case Test",
                        TargetClientName = "roku"
                    }
                }
            };

            // Act & Assert
            FindMatchingProfile(config, "ROKU", "device1", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "rOkU", "device1", "192.168.1.100").Should().NotBeNull();
        }

        #endregion

        #region Device ID Filtering Tests

        [Fact]
        public void ProfileMatching_SingleDeviceId_ShouldMatchExactDevice()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Single Device",
                        TargetDeviceIds = new List<string> { "roku-123" }
                    }
                }
            };

            // Act & Assert
            FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_MultipleDeviceIds_ShouldMatchAny()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Multiple Devices",
                        TargetDeviceIds = new List<string> { "roku-123", "firetv-456", "appletv-789" }
                    }
                }
            };

            // Act & Assert
            FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "FireTV", "firetv-456", "192.168.1.101").Should().NotBeNull();
            FindMatchingProfile(config, "AppleTV", "appletv-789", "192.168.1.102").Should().NotBeNull();
            FindMatchingProfile(config, "Web", "web-browser", "192.168.1.103").Should().BeNull();
        }

        [Theory]
        [InlineData("roku-123", "roku-123", true)]
        [InlineData("roku-123", "ROKU-123", true)]  // Case insensitive
        [InlineData("roku-123", "roku-456", false)]
        public void ProfileMatching_DeviceId_ShouldBeCaseInsensitive(
            string configuredId, string actualId, bool shouldMatch)
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Case Test",
                        TargetDeviceIds = new List<string> { configuredId }
                    }
                }
            };

            // Act
            var profile = FindMatchingProfile(config, "Roku", actualId, "192.168.1.100");

            // Assert
            if (shouldMatch)
                profile.Should().NotBeNull();
            else
                profile.Should().BeNull();
        }

        #endregion

        #region IP Address Filtering Tests

        [Fact]
        public void ProfileMatching_IpAddressOnly_ShouldMatchExactIp()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "IP Filter",
                        TargetIpAddress = "192.168.1.100"
                    }
                }
            };

            // Act & Assert
            FindMatchingProfile(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Roku", "device1", "192.168.1.101").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_IpAddress_ShouldStripPort()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "IP Filter",
                        TargetIpAddress = "192.168.1.100"
                    }
                }
            };

            // Act & Assert - Should match even with port
            FindMatchingProfile(config, "Roku", "device1", "192.168.1.100:8096").Should().NotBeNull();
        }

        #endregion

        #region Combined Filter Tests (AND Logic)

        [Fact]
        public void ProfileMatching_ClientAndDevice_ShouldRequireBoth()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Combined",
                        TargetClientName = "Roku",
                        TargetDeviceIds = new List<string> { "roku-123" }
                    }
                }
            };

            // Act & Assert - Must match BOTH filters
            FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull(); // Wrong device
            FindMatchingProfile(config, "Web", "roku-123", "192.168.1.100").Should().BeNull(); // Wrong client
        }

        [Fact]
        public void ProfileMatching_AllThreeFilters_ShouldRequireAll()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Triple Filter",
                        TargetClientName = "Roku",
                        TargetDeviceIds = new List<string> { "roku-123" },
                        TargetIpAddress = "192.168.1.100"
                    }
                }
            };

            // Act & Assert - Must match ALL three filters
            FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.101").Should().BeNull(); // Wrong IP
            FindMatchingProfile(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull(); // Wrong device
            FindMatchingProfile(config, "Web", "roku-123", "192.168.1.100").Should().BeNull(); // Wrong client
        }

        #endregion

        #region Priority/Ordering Tests

        [Fact]
        public void ProfileMatching_FirstMatchWins_ShouldReturnFirstProfile()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile { Name = "Profile1", TargetClientName = "Roku" },
                    new LightControlProfile { Name = "Profile2", TargetClientName = "Roku" }
                }
            };

            // Act
            var profile = FindMatchingProfile(config, "Roku", "device1", "192.168.1.100");

            // Assert - Should return first matching profile
            profile.Should().NotBeNull();
            profile!.Name.Should().Be("Profile1");
        }

        [Fact]
        public void ProfileMatching_MoreSpecificFirst_ShouldMatchSpecific()
        {
            // Arrange
            var config = new PluginConfiguration
            {
                EnablePlugin = true,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile 
                    { 
                        Name = "Specific", 
                        TargetClientName = "Roku",
                        TargetDeviceIds = new List<string> { "roku-123" }
                    },
                    new LightControlProfile 
                    { 
                        Name = "General", 
                        TargetClientName = "Roku"
                    }
                }
            };

            // Act
            var profile1 = FindMatchingProfile(config, "Roku", "roku-123", "192.168.1.100");
            var profile2 = FindMatchingProfile(config, "Roku", "roku-456", "192.168.1.100");

            // Assert
            profile1.Should().NotBeNull();
            profile1!.Name.Should().Be("Specific"); // Matches first (more specific) profile

            profile2.Should().NotBeNull();
            profile2!.Name.Should().Be("General"); // Falls through to second profile
        }

        #endregion

        #region Helper Methods

        private LightControlProfile? FindMatchingProfile(PluginConfiguration config, string clientName, string deviceId, string remoteEndpoint)
        {
            if (!config.EnablePlugin)
            {
                return null;
            }

            if (config.Profiles == null || config.Profiles.Count == 0)
            {
                return null;
            }

            foreach (var profile in config.Profiles)
            {
                if (ProfileMatches(profile, clientName, deviceId, remoteEndpoint))
                {
                    return profile;
                }
            }

            return null;
        }

        private bool ProfileMatches(LightControlProfile profile, string clientName, string deviceId, string remoteEndpoint)
        {
            bool hasClientFilter = !string.IsNullOrWhiteSpace(profile.TargetClientName);
            bool hasDeviceFilter = profile.TargetDeviceIds != null && profile.TargetDeviceIds.Count > 0;
            bool hasIpFilter = !string.IsNullOrWhiteSpace(profile.TargetIpAddress);

            // If no filters in profile, it matches everything
            if (!hasClientFilter && !hasDeviceFilter && !hasIpFilter)
            {
                return true;
            }

            // Check IP address (most specific)
            if (hasIpFilter)
            {
                var clientIp = ExtractIpAddress(remoteEndpoint);
                if (!clientIp.Equals(profile.TargetIpAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check Device ID list
            if (hasDeviceFilter)
            {
                bool deviceMatches = profile.TargetDeviceIds?.Any(id =>
                    deviceId.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? false;
                if (!deviceMatches)
                {
                    return false;
                }
            }

            // Check ClientName (least specific, substring match)
            if (hasClientFilter)
            {
                if (!clientName.Contains(profile.TargetClientName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private string ExtractIpAddress(string remoteEndpoint)
        {
            if (string.IsNullOrWhiteSpace(remoteEndpoint))
            {
                return string.Empty;
            }

            // Remote endpoint format is usually "IP:PORT" or just "IP"
            var colonIndex = remoteEndpoint.IndexOf(':');
            if (colonIndex > 0)
            {
                return remoteEndpoint.Substring(0, colonIndex);
            }

            return remoteEndpoint;
        }

        #endregion
    }
}
