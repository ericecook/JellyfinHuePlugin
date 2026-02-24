using System;
using System.Collections.Generic;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using Xunit;

namespace JellyfinHuePlugin.Tests.Configuration
{
    public class PluginConfigurationTests
    {
        [Fact]
        public void DefaultConfiguration_ShouldHaveExpectedValues()
        {
            // Arrange & Act
            var config = new PluginConfiguration();

            // Assert
            config.BridgeIpAddress.Should().BeEmpty();
            config.Username.Should().BeEmpty();
            config.EnablePlugin.Should().BeTrue();
            config.Profiles.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void ProfilesList_ShouldBeModifiable()
        {
            // Arrange
            var config = new PluginConfiguration();

            // Act
            config.Profiles.Add(new LightControlProfile { Name = "Profile1" });
            config.Profiles.Add(new LightControlProfile { Name = "Profile2" });

            // Assert
            config.Profiles.Should().HaveCount(2);
            config.Profiles[0].Name.Should().Be("Profile1");
            config.Profiles[1].Name.Should().Be("Profile2");
        }

        [Fact]
        public void Profile_ShouldHaveDefaultValues()
        {
            // Arrange & Act
            var profile = new LightControlProfile();

            // Assert
            profile.Id.Should().NotBeNullOrEmpty(); // GUID is generated
            profile.Name.Should().Be("Default Profile");
            profile.EnableForMovies.Should().BeTrue(); // Default: Movies enabled
            profile.EnableForTvShows.Should().BeFalse(); // Default: TV disabled
            profile.TargetClientName.Should().BeEmpty();
            profile.TargetDeviceIds.Should().NotBeNull().And.BeEmpty();
            profile.TargetIpAddress.Should().BeEmpty();
            profile.TargetGroupId.Should().Be("0");
            profile.PlaySceneId.Should().BeEmpty();
            profile.PauseSceneId.Should().BeEmpty();
            profile.StopSceneId.Should().BeEmpty();
            profile.TurnOffLightsOnPlay.Should().BeFalse();
            profile.PlayBrightness.Should().Be(20);
            profile.PauseBrightness.Should().Be(100);
            profile.StopBrightness.Should().Be(254);
            profile.EnableOutroLights.Should().BeFalse(); // Default: Outro detection disabled
        }

        [Fact]
        public void Profile_DeviceIdsList_ShouldBeModifiable()
        {
            // Arrange
            var profile = new LightControlProfile();

            // Act
            profile.TargetDeviceIds.Add("device1");
            profile.TargetDeviceIds.Add("device2");

            // Assert
            profile.TargetDeviceIds.Should().HaveCount(2);
            profile.TargetDeviceIds.Should().Contain("device1");
            profile.TargetDeviceIds.Should().Contain("device2");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(127)]
        [InlineData(254)]
        public void Profile_BrightnessValues_ShouldAcceptValidRange(int brightness)
        {
            // Arrange
            var profile = new LightControlProfile();

            // Act
            profile.PlayBrightness = brightness;
            profile.PauseBrightness = brightness;
            profile.StopBrightness = brightness;

            // Assert
            profile.PlayBrightness.Should().Be(brightness);
            profile.PauseBrightness.Should().Be(brightness);
            profile.StopBrightness.Should().Be(brightness);
        }

        [Fact]
        public void Profile_BrightnessZero_ShouldBePreserved()
        {
            // Arrange
            var profile = new LightControlProfile();

            // Act
            profile.PlayBrightness = 0;

            // Assert
            profile.PlayBrightness.Should().Be(0);
        }

        [Fact]
        public void Profile_SceneIds_CanBeSetAndCleared()
        {
            // Arrange
            var profile = new LightControlProfile();

            // Act
            profile.PlaySceneId = "scene1";
            profile.PauseSceneId = "scene2";
            profile.StopSceneId = "scene3";

            // Assert
            profile.PlaySceneId.Should().Be("scene1");
            profile.PauseSceneId.Should().Be("scene2");
            profile.StopSceneId.Should().Be("scene3");

            // Act - Clear
            profile.PlaySceneId = string.Empty;

            // Assert
            profile.PlaySceneId.Should().BeEmpty();
        }

        [Fact]
        public void Profile_CanSetAllProperties()
        {
            // Arrange & Act
            var profile = new LightControlProfile
            {
                Name = "Test Profile",
                EnableForMovies = false,
                EnableForTvShows = true,
                TargetClientName = "Roku",
                TargetDeviceIds = new List<string> { "roku-123", "roku-456" },
                TargetIpAddress = "192.168.1.100",
                TargetGroupId = "1",
                PlaySceneId = "play-scene",
                PauseSceneId = "pause-scene",
                StopSceneId = "stop-scene",
                TurnOffLightsOnPlay = true,
                PlayBrightness = 10,
                PauseBrightness = 50,
                StopBrightness = 254
            };

            // Assert
            profile.Name.Should().Be("Test Profile");
            profile.EnableForMovies.Should().BeFalse();
            profile.EnableForTvShows.Should().BeTrue();
            profile.TargetClientName.Should().Be("Roku");
            profile.TargetDeviceIds.Should().HaveCount(2);
            profile.TargetIpAddress.Should().Be("192.168.1.100");
            profile.TargetGroupId.Should().Be("1");
            profile.PlaySceneId.Should().Be("play-scene");
            profile.TurnOffLightsOnPlay.Should().BeTrue();
            profile.PlayBrightness.Should().Be(10);
        }

        [Theory]
        [InlineData(true, false, "Movies only")]
        [InlineData(false, true, "TV shows only")]
        [InlineData(true, true, "Both movies and TV shows")]
        public void Profile_MediaTypeConfiguration_ShouldBeConfigurable(bool movies, bool tv, string description)
        {
            // Arrange & Act
            var profile = new LightControlProfile
            {
                EnableForMovies = movies,
                EnableForTvShows = tv
            };

            // Assert
            profile.EnableForMovies.Should().Be(movies, description);
            profile.EnableForTvShows.Should().Be(tv, description);
        }

        [Fact]
        public void Profile_OutroDetection_CanBeEnabled()
        {
            // Arrange & Act
            var profile = new LightControlProfile
            {
                EnableOutroLights = true,
                StopBrightness = 150,
                StopSceneId = "credits-scene"
            };

            // Assert
            profile.EnableOutroLights.Should().BeTrue();
            profile.StopBrightness.Should().Be(150);
            profile.StopSceneId.Should().Be("credits-scene");
        }

        [Fact]
        public void Profile_OutroDetection_DefaultsToDisabled()
        {
            // Arrange & Act
            var profile = new LightControlProfile();

            // Assert
            profile.EnableOutroLights.Should().BeFalse("outro detection should be opt-in");
        }
    }
}
