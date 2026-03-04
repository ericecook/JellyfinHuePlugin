using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
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

        [Fact]
        public void XmlDeserialization_OldConfigFormat_ShouldPreserveAllSettings()
        {
            // Simulate a config XML from before the TransitionDuration refactor
            var oldXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <BridgeIpAddress>192.168.1.50</BridgeIpAddress>
  <BridgeId>001788FFFE123456</BridgeId>
  <Username>abc123</Username>
  <EnablePlugin>true</EnablePlugin>
  <UseLightGroups>true</UseLightGroups>
  <Profiles>
    <LightControlProfile>
      <Id>test-id-1</Id>
      <Name>Living Room</Name>
      <EnableForMovies>true</EnableForMovies>
      <EnableForTvShows>true</EnableForTvShows>
      <TargetClientName>Roku</TargetClientName>
      <TargetDeviceIds>
        <string>device-123</string>
      </TargetDeviceIds>
      <TargetIpAddress>192.168.1.100</TargetIpAddress>
      <TargetGroupId>1</TargetGroupId>
      <PlayBrightness>10</PlayBrightness>
      <PauseBrightness>80</PauseBrightness>
      <StopBrightness>200</StopBrightness>
      <TransitionDuration>8</TransitionDuration>
      <EnableOutroLights>true</EnableOutroLights>
    </LightControlProfile>
  </Profiles>
</PluginConfiguration>";

            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var reader = new StringReader(oldXml);
            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            // Top-level settings preserved
            config.BridgeIpAddress.Should().Be("192.168.1.50");
            config.Username.Should().Be("abc123");
            config.EnablePlugin.Should().BeTrue();
            config.Profiles.Should().HaveCount(1);

            // Profile settings preserved
            var p = config.Profiles[0];
            p.Id.Should().Be("test-id-1");
            p.Name.Should().Be("Living Room");
            p.EnableForMovies.Should().BeTrue();
            p.EnableForTvShows.Should().BeTrue();
            p.TargetClientName.Should().Be("Roku");
            p.TargetDeviceIds.Should().ContainSingle("device-123");
            p.TargetIpAddress.Should().Be("192.168.1.100");
            p.TargetGroupId.Should().Be("1");
            p.PlayBrightness.Should().Be(10);
            p.PauseBrightness.Should().Be(80);
            p.StopBrightness.Should().Be(200);
            p.EnableOutroLights.Should().BeTrue();

            // Legacy TransitionDuration migrated to per-state settings
            p.PlayTransitionDuration.Should().Be(8);
            p.PauseTransitionDuration.Should().Be(8);
            p.StopTransitionDuration.Should().Be(8);
            p.EnablePlayTransition.Should().BeTrue();
            p.EnablePauseTransition.Should().BeTrue();
            p.EnableStopTransition.Should().BeTrue();
        }

        [Fact]
        public void XmlDeserialization_NewConfigFormat_ShouldWork()
        {
            // Config XML with the new per-state transition properties
            var newXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <BridgeIpAddress>10.0.0.5</BridgeIpAddress>
  <Username>newuser</Username>
  <EnablePlugin>true</EnablePlugin>
  <Profiles>
    <LightControlProfile>
      <Id>new-id</Id>
      <Name>Bedroom</Name>
      <EnableForMovies>true</EnableForMovies>
      <EnablePlayTransition>true</EnablePlayTransition>
      <PlayTransitionDuration>10</PlayTransitionDuration>
      <EnablePauseTransition>false</EnablePauseTransition>
      <PauseTransitionDuration>4</PauseTransitionDuration>
    </LightControlProfile>
  </Profiles>
</PluginConfiguration>";

            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var reader = new StringReader(newXml);
            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            config.BridgeIpAddress.Should().Be("10.0.0.5");
            config.Profiles.Should().HaveCount(1);

            var p = config.Profiles[0];
            p.EnablePlayTransition.Should().BeTrue();
            p.PlayTransitionDuration.Should().Be(10);
            p.EnablePauseTransition.Should().BeFalse();
            p.PauseTransitionDuration.Should().Be(4);
        }
    }
}
