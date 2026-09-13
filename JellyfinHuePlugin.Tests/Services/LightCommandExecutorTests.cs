using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// The executor is the only code that talks to HueService for playback. These tests pin
    /// the exact v2 calls per action: percent brightness, millisecond transitions, scene recall,
    /// and the catalog resolving the profile's ids.
    /// </summary>
    public class LightCommandExecutorTests
    {
        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly List<GroupedLightState> _sent = new();
        private readonly LightCommandExecutor _executor;
        private readonly HueBridge _bridge = new() { Id = "bridge1", Name = "Test Bridge", IpAddress = "192.168.1.50", Username = "testuser", HardwareId = "001788fffe123456" };

        public LightCommandExecutorTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, s, _) => _sent.Add(s))
                .ReturnsAsync(true);
            _hue.Setup(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger.Instance) { CallBase = false };
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string target, CancellationToken _) => "gl-" + target);
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string scene, CancellationToken _) => "resolved-" + scene);

            _executor = new LightCommandExecutor(_hue.Object, _catalog.Object, NullLogger.Instance);
        }

        private static LightControlProfile MakeProfile() => new()
        {
            Name = "Test Profile",
            BridgeId = "bridge1",
            PlayBrightness = 20,
            PauseBrightness = 60,
            StopBrightness = 100,
            TargetGroupId = "1"
        };

        private Task Execute(LightAction action, LightControlProfile profile, CancellationToken token = default)
            => _executor.ExecuteAsync(action, _bridge, profile, token);

        private void VerifyGroupedLight(Func<GroupedLightState, bool> match, Times times) =>
            _hue.Verify(h => h.SetGroupedLightAsync(_bridge, "gl-1", It.Is<GroupedLightState>(s => match(s)), It.IsAny<CancellationToken>()), times);

        private void VerifyRecall(string sceneId, int? durationMs, Times times) =>
            _hue.Verify(h => h.RecallSceneAsync(_bridge, "resolved-" + sceneId, durationMs, It.IsAny<CancellationToken>()), times);

        [Fact]
        public async Task Play_Brightness_SendsPercentAndDurationInMilliseconds()
        {
            var profile = MakeProfile();
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 7;

            await Execute(LightAction.Play, profile);

            VerifyGroupedLight(s => s.On == true && s.Brightness == 20 && s.DurationMs == 700, Times.Once());
        }

        [Fact]
        public async Task Play_Brightness_NoTransition_OmitsDuration()
        {
            await Execute(LightAction.Play, MakeProfile());

            _sent.Should().ContainSingle().Which.DurationMs.Should().BeNull();
        }

        [Fact]
        public async Task Play_Scene_RecallsWithDuration()
        {
            var profile = MakeProfile();
            profile.PlaySceneId = "scene-play";
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 4;

            await Execute(LightAction.Play, profile);

            VerifyRecall("scene-play", 400, Times.Once());
            _sent.Should().BeEmpty();
        }

        [Fact]
        public async Task Play_TurnOff_NoTransition_SendsOffOnly()
        {
            var profile = MakeProfile();
            profile.TurnOffLightsOnPlay = true;

            await Execute(LightAction.Play, profile);

            var state = _sent.Should().ContainSingle().Subject;
            state.On.Should().BeFalse();
            state.Brightness.Should().BeNull();
            state.DurationMs.Should().BeNull();
        }

        [Fact]
        public async Task Play_TurnOff_WithTransition_IsOneFadeOutRequest()
        {
            var profile = MakeProfile();
            profile.TurnOffLightsOnPlay = true;
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 50;

            await Execute(LightAction.Play, profile);

            var state = _sent.Should().ContainSingle().Subject;
            state.On.Should().BeFalse();
            state.DurationMs.Should().Be(5000);
        }

        [Fact]
        public async Task Pause_Brightness_SendsPercent()
        {
            var profile = MakeProfile();
            profile.EnablePauseTransition = true;
            profile.PauseTransitionDuration = 3;

            await Execute(LightAction.Pause, profile);

            VerifyGroupedLight(s => s.On == true && s.Brightness == 60 && s.DurationMs == 300, Times.Once());
        }

        [Fact]
        public async Task Pause_Scene_Recalls()
        {
            var profile = MakeProfile();
            profile.PauseSceneId = "scene-pause";

            await Execute(LightAction.Pause, profile);

            VerifyRecall("scene-pause", null, Times.Once());
        }

        [Fact]
        public async Task Stop_Brightness_SendsPercent()
        {
            await Execute(LightAction.Stop, MakeProfile());

            VerifyGroupedLight(s => s.On == true && s.Brightness == 100 && s.DurationMs == null, Times.Once());
        }

        [Fact]
        public async Task Stop_Scene_RecallsWithDuration()
        {
            var profile = MakeProfile();
            profile.StopSceneId = "scene-stop";
            profile.EnableStopTransition = true;
            profile.StopTransitionDuration = 9;

            await Execute(LightAction.Stop, profile);

            VerifyRecall("scene-stop", 900, Times.Once());
        }

        [Fact]
        public async Task BridgeUnconfigured_SendsNothing()
        {
            var bridge = new HueBridge { Id = "b", Name = "Empty", IpAddress = "", Username = "" };

            await _executor.ExecuteAsync(LightAction.Play, bridge, MakeProfile(), CancellationToken.None);

            _hue.VerifyNoOtherCalls();
            _catalog.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task UnresolvedTargetGroup_SendsNothing()
        {
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            await Execute(LightAction.Play, MakeProfile());

            _sent.Should().BeEmpty();
        }

        [Fact]
        public async Task UnresolvedScene_SendsNothing()
        {
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            var profile = MakeProfile();
            profile.StopSceneId = "gone";

            await Execute(LightAction.Stop, profile);

            _hue.Verify(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
            _sent.Should().BeEmpty();
        }

        [Fact]
        public async Task HueServiceThrows_IsSwallowed()
        {
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("bridge offline"));

            var act = () => Execute(LightAction.Stop, MakeProfile());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task PassesTokenToHueService()
        {
            using var cts = new CancellationTokenSource();

            await Execute(LightAction.Pause, MakeProfile(), cts.Token);

            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(),
                It.Is<CancellationToken>(t => t == cts.Token)), Times.Once);
        }

        [Fact]
        public async Task CancelledToken_Propagates()
        {
            using var cts = new CancellationTokenSource();
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Returns<HueBridge, string, GroupedLightState, CancellationToken>((_, _, _, token) =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(token);
                });

            var act = () => Execute(LightAction.Stop, MakeProfile(), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
