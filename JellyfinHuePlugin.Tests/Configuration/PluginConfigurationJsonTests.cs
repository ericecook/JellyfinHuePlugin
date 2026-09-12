using System.Text.Json;
using FluentAssertions;
using Jellyfin.Extensions.Json;
using JellyfinHuePlugin.Configuration;
using Xunit;

namespace JellyfinHuePlugin.Tests.Configuration
{
    /// <summary>
    /// The config page saves through Jellyfin's JSON API
    /// (GET/POST /Plugins/{id}/Configuration), which uses System.Text.Json with
    /// <see cref="JsonDefaults.Options"/>. The XmlSerializer-only ShouldSerialize*
    /// convention does nothing there, so these tests exercise the real round-trip.
    /// </summary>
    public class PluginConfigurationJsonTests
    {
        private static readonly JsonSerializerOptions Options = JsonDefaults.Options;

        private static LightControlProfile MakeProfile() => new LightControlProfile
        {
            Name = "Round trip",
            EnablePlayTransition = true,
            PlayTransitionDuration = 30,
            EnablePauseTransition = false,
            PauseTransitionDuration = 12,
            EnableStopTransition = true,
            StopTransitionDuration = 50,
        };

        [Fact]
        public void JsonRoundTrip_ShouldPreservePerStateTransitionSettings()
        {
            var config = new PluginConfiguration();
            config.Profiles.Add(MakeProfile());

            var json = JsonSerializer.Serialize(config, Options);
            var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, Options)!;

            var p = restored.Profiles.Should().ContainSingle().Subject;
            p.EnablePlayTransition.Should().BeTrue();
            p.PlayTransitionDuration.Should().Be(30);
            p.EnablePauseTransition.Should().BeFalse();
            p.PauseTransitionDuration.Should().Be(12);
            p.EnableStopTransition.Should().BeTrue();
            p.StopTransitionDuration.Should().Be(50);
        }

        [Fact]
        public void JsonSerialization_ShouldNotEmitLegacyProperties()
        {
            var config = new PluginConfiguration();
            config.Profiles.Add(MakeProfile());

            var json = JsonSerializer.Serialize(config, Options);

            json.Should().NotContain("LegacyTransitionDuration");
            json.Should().NotContain("TransitionDuration\":0");
            json.Should().NotContain("LegacyBridgeIpAddress");
            json.Should().NotContain("LegacyUsername");
            json.Should().NotContain("LegacyBridgeId");
            json.Should().NotContain("UseLightGroups");
        }

        [Fact]
        public void JsonDeserialization_StaleClientLegacyDuration_ShouldNotClobberPerStateValues()
        {
            // Exact regression shape: a client that loaded the config before this fix
            // posts back "LegacyTransitionDuration":0 after the real values.
            var json = @"{""Profiles"":[{""Name"":""x"",""EnablePlayTransition"":true,""PlayTransitionDuration"":30,""EnablePauseTransition"":false,""PauseTransitionDuration"":12,""EnableStopTransition"":true,""StopTransitionDuration"":50,""LegacyTransitionDuration"":0,""UseLightGroups"":false}]}";

            var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, Options)!;

            var p = restored.Profiles.Should().ContainSingle().Subject;
            p.PlayTransitionDuration.Should().Be(30);
            p.PauseTransitionDuration.Should().Be(12);
            p.StopTransitionDuration.Should().Be(50);
            p.EnablePauseTransition.Should().BeFalse();
        }

        [Fact]
        public void JsonDeserialization_NullTransitionDuration_ShouldThrow()
        {
            // This is what the browser sends when a number input is cleared:
            // parseInt('') is NaN and JSON.stringify turns NaN into null.
            var json = @"{""Profiles"":[{""Name"":""x"",""PlayTransitionDuration"":null}]}";

            var act = () => JsonSerializer.Deserialize<PluginConfiguration>(json, Options);

            act.Should().Throw<JsonException>(
                "a non-nullable int rejects null, so the config page must never send it");
        }
    }
}
