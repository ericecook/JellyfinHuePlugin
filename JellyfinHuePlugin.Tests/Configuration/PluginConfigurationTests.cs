using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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
            config.Bridges.Should().NotBeNull().And.BeEmpty();
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
            profile.PlayBrightness.Should().Be(8);
            profile.PauseBrightness.Should().Be(39);
            profile.StopBrightness.Should().Be(100);
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
        [InlineData(50)]
        [InlineData(100)]
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

            // Legacy bridge fields should be absorbed; migrate to Bridges list
            config.MigrateLegacyConfig();
            config.Bridges.Should().HaveCount(1);
            config.Bridges[0].IpAddress.Should().Be("192.168.1.50");
            config.Bridges[0].Username.Should().Be("abc123");
            config.EnablePlugin.Should().BeTrue();
            config.Profiles.Should().HaveCount(1);
            config.Profiles[0].BridgeId.Should().Be(config.Bridges[0].Id);

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
        public void XmlSerialization_ShouldNotWriteLegacyElements()
        {
            // Jellyfin persists config with XmlSerializer; the ShouldSerialize* methods must
            // keep suppressing the legacy elements alongside the [JsonIgnore] attributes.
            var config = new PluginConfiguration();
            var bridge = new HueBridge { Name = "B", IpAddress = "10.0.0.5", Username = "u" };
            config.Bridges.Add(bridge);
            config.Profiles.Add(new LightControlProfile { Name = "P", BridgeId = bridge.Id, EnablePlayTransition = true, PlayTransitionDuration = 30 });

            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var writer = new StringWriter();
            serializer.Serialize(writer, config);
            var xml = writer.ToString();

            var doc = XDocument.Parse(xml);
            var topLevel = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
            topLevel.Should().NotContain(new[] { "BridgeIpAddress", "Username", "BridgeId", "UseLightGroups" });
            topLevel.Should().Contain(new[] { "Bridges", "Profiles", "EnablePlugin" });

            var profile = doc.Root.Element("Profiles")!.Elements().Single();
            var profileElements = profile.Elements().Select(e => e.Name.LocalName).ToList();
            profileElements.Should().NotContain("TransitionDuration");
            profile.Element("PlayTransitionDuration")!.Value.Should().Be("30");

            using var reader = new StringReader(xml);
            var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
            restored.Profiles[0].PlayTransitionDuration.Should().Be(30);
            restored.Profiles[0].BridgeId.Should().Be(bridge.Id);
            restored.Bridges[0].Username.Should().Be("u");
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

            // Legacy bridge fields migrated
            config.MigrateLegacyConfig();
            config.Bridges.Should().HaveCount(1);
            config.Bridges[0].IpAddress.Should().Be("10.0.0.5");
            config.Profiles.Should().HaveCount(1);

            var p = config.Profiles[0];
            p.EnablePlayTransition.Should().BeTrue();
            p.PlayTransitionDuration.Should().Be(10);
            p.EnablePauseTransition.Should().BeFalse();
            p.PauseTransitionDuration.Should().Be(4);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(10, 4)]
        [InlineData(20, 8)]
        [InlineData(80, 31)]
        [InlineData(100, 39)]
        [InlineData(127, 50)]
        [InlineData(200, 79)]
        [InlineData(254, 100)]
        [InlineData(300, 100)]
        public void ToPercent_ScalesAndClamps(int v1, int expected)
        {
            PluginConfiguration.ToPercent(v1).Should().Be(expected);
        }

        [Fact]
        public void MigrateBrightnessToPercent_ConvertsEveryProfileOnce()
        {
            var config = new PluginConfiguration();
            config.Profiles.Add(new LightControlProfile { PlayBrightness = 20, PauseBrightness = 100, StopBrightness = 254 });
            config.Profiles.Add(new LightControlProfile { PlayBrightness = 0, PauseBrightness = 127, StopBrightness = 200 });

            config.MigrateBrightnessToPercent().Should().BeTrue();

            config.SchemaVersion.Should().Be(2);
            config.Profiles[0].PlayBrightness.Should().Be(8);
            config.Profiles[0].PauseBrightness.Should().Be(39);
            config.Profiles[0].StopBrightness.Should().Be(100);
            config.Profiles[1].PlayBrightness.Should().Be(0);
            config.Profiles[1].PauseBrightness.Should().Be(50);
            config.Profiles[1].StopBrightness.Should().Be(79);

            config.MigrateBrightnessToPercent().Should().BeFalse();
            config.Profiles[0].PlayBrightness.Should().Be(8); // not scaled twice
        }

        [Fact]
        public void MigrateBrightnessToPercent_FreshConfiguration_StampsTheVersionOnce()
        {
            var config = new PluginConfiguration();

            config.SchemaVersion.Should().Be(0);
            config.MigrateBrightnessToPercent().Should().BeTrue();
            config.SchemaVersion.Should().Be(2);
            config.MigrateBrightnessToPercent().Should().BeFalse();
        }

        [Fact]
        public void XmlDeserialization_WithoutSchemaVersion_ReadsAsZero()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <Bridges />
  <Profiles>
    <LightControlProfile>
      <Name>Old</Name>
      <PlayBrightness>20</PlayBrightness>
      <PauseBrightness>100</PauseBrightness>
      <StopBrightness>254</StopBrightness>
    </LightControlProfile>
  </Profiles>
  <EnablePlugin>true</EnablePlugin>
</PluginConfiguration>";
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using var reader = new System.IO.StringReader(xml);

            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            config.SchemaVersion.Should().Be(0);
            config.MigrateBrightnessToPercent().Should().BeTrue();
            config.Profiles[0].StopBrightness.Should().Be(100);
        }

        [Fact]
        public void XmlRoundTrip_KeepsSchemaVersion()
        {
            var config = new PluginConfiguration { SchemaVersion = 2 };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, config);
            using var reader = new System.IO.StringReader(writer.ToString());

            var back = (PluginConfiguration)serializer.Deserialize(reader)!;

            back.SchemaVersion.Should().Be(2);
            back.MigrateBrightnessToPercent().Should().BeFalse();
        }

        [Fact]
        public void FindProfileBridge_ExplicitIdMatching_ReturnsThatBridge()
        {
            var config = new PluginConfiguration();
            var bridge1 = new HueBridge { Id = "b1" };
            var bridge2 = new HueBridge { Id = "b2" };
            config.Bridges.Add(bridge1);
            config.Bridges.Add(bridge2);
            var profile = new LightControlProfile { BridgeId = "b2" };

            config.FindProfileBridge(profile).Should().BeSameAs(bridge2);
        }

        [Fact]
        public void FindProfileBridge_ExplicitIdNotFound_ReturnsNull()
        {
            var config = new PluginConfiguration();
            config.Bridges.Add(new HueBridge { Id = "b1" });
            var profile = new LightControlProfile { BridgeId = "nope" };

            config.FindProfileBridge(profile).Should().BeNull();
        }

        [Fact]
        public void FindProfileBridge_EmptyIdWithExactlyOneBridge_ReturnsThatBridge()
        {
            var config = new PluginConfiguration();
            var bridge = new HueBridge { Id = "b1" };
            config.Bridges.Add(bridge);
            var profile = new LightControlProfile { BridgeId = "" };

            config.FindProfileBridge(profile).Should().BeSameAs(bridge);
        }

        [Fact]
        public void FindProfileBridge_EmptyIdWithTwoBridges_ReturnsNull()
        {
            var config = new PluginConfiguration();
            config.Bridges.Add(new HueBridge { Id = "b1" });
            config.Bridges.Add(new HueBridge { Id = "b2" });
            var profile = new LightControlProfile { BridgeId = "" };

            config.FindProfileBridge(profile).Should().BeNull();
        }

        [Fact]
        public void FindProfileBridge_EmptyIdWithNoBridges_ReturnsNull()
        {
            var config = new PluginConfiguration();
            var profile = new LightControlProfile { BridgeId = "" };

            config.FindProfileBridge(profile).Should().BeNull();
        }

        [Fact]
        public void HueBridge_HardwareId_DefaultsToEmpty()
        {
            new HueBridge().HardwareId.Should().BeEmpty();
        }

        [Fact]
        public void HueBridge_XmlDeserialization_OldBridgeIdElement_ReadsIntoHardwareId()
        {
            // HueBridge.HardwareId was renamed from BridgeId; [XmlElement("BridgeId")] keeps the
            // on-disk element name so a configuration written before the rename still loads
            // instead of silently dropping the bridge's pinned hardware id.
            var oldXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <Bridges>
    <HueBridge>
      <Id>bridge1</Id>
      <Name>Living Room</Name>
      <IpAddress>192.168.1.50</IpAddress>
      <Username>abc123</Username>
      <BridgeId>001788fffe123456</BridgeId>
    </HueBridge>
  </Bridges>
</PluginConfiguration>";

            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var reader = new StringReader(oldXml);
            var config = (PluginConfiguration)serializer.Deserialize(reader)!;

            config.Bridges.Should().ContainSingle().Which.HardwareId.Should().Be("001788fffe123456");
        }

        [Fact]
        public void RemoveInvalidEntries_DropsNullBridgesProfilesAndDeviceIds()
        {
            var bridge = new HueBridge { Name = "Bridge" };
            var profile = new LightControlProfile { Name = "p", TargetDeviceIds = new List<string> { "device-1", null! } };
            var config = new PluginConfiguration
            {
                Bridges = new List<HueBridge> { null!, bridge },
                Profiles = new List<LightControlProfile> { profile, null!, null! }
            };

            var repaired = config.RemoveInvalidEntries();

            repaired.Should().Be(4);
            config.Bridges.Should().ContainSingle().Which.Should().BeSameAs(bridge);
            config.Profiles.Should().ContainSingle().Which.Should().BeSameAs(profile);
            profile.TargetDeviceIds.Should().Equal("device-1");
        }

        [Fact]
        public void RemoveInvalidEntries_ReplacesMissingListsWithEmptyOnes()
        {
            var profile = new LightControlProfile { Name = "p", TargetDeviceIds = null! };
            var config = new PluginConfiguration { Bridges = null!, Profiles = new List<LightControlProfile> { profile } };

            config.RemoveInvalidEntries().Should().Be(2);
            config.Bridges.Should().NotBeNull().And.BeEmpty();
            profile.TargetDeviceIds.Should().NotBeNull().And.BeEmpty();

            var noProfiles = new PluginConfiguration { Profiles = null! };
            noProfiles.RemoveInvalidEntries().Should().Be(1);
            noProfiles.Profiles.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void RemoveInvalidEntries_CleanConfiguration_ReturnsZero()
        {
            var config = new PluginConfiguration
            {
                Bridges = new List<HueBridge> { new() { Name = "Bridge" } },
                Profiles = new List<LightControlProfile> { new() { Name = "p" } }
            };

            config.RemoveInvalidEntries().Should().Be(0);
            config.Bridges.Should().HaveCount(1);
            config.Profiles.Should().HaveCount(1);
        }
    }
}
