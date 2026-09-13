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
    /// the exact calls per action and the cancellable turn-off wait.
    /// </summary>
    public class LightCommandExecutorTests
    {
        private readonly Mock<HueService> _hue;
        private readonly List<HueLightState> _sent = new();
        private readonly LightCommandExecutor _executor;
        private readonly HueBridge _bridge = new() { Id = "bridge1", Name = "Test Bridge", IpAddress = "192.168.1.50", Username = "testuser" };

        public LightCommandExecutorTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.SetGroupStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, HueLightState, CancellationToken>((_, _, _, s, _) => _sent.Add(s))
                .ReturnsAsync(true);
            _hue.Setup(h => h.ActivateSceneAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _executor = new LightCommandExecutor(_hue.Object, NullLogger.Instance, TimeProvider.System);
        }

        private static LightControlProfile MakeProfile() => new()
        {
            Name = "Test Profile",
            BridgeId = "bridge1",
            PlayBrightness = 20,
            PauseBrightness = 100,
            StopBrightness = 254,
            TargetGroupId = "1"
        };

        private Task Execute(LightAction action, LightControlProfile profile, CancellationToken token = default)
            => _executor.ExecuteAsync(action, _bridge, profile, token);

        private void VerifyGroupState(Func<HueLightState, bool> match, Times times) =>
            _hue.Verify(h => h.SetGroupStateAsync("192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => match(s)), It.IsAny<CancellationToken>()), times);

        [Fact]
        public async Task Play_Brightness_SetsGroupStateWithTransition()
        {
            var profile = MakeProfile();
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 7;

            await Execute(LightAction.Play, profile);

            VerifyGroupState(s => s.On == true && s.Bri == 20 && s.TransitionTime == 7, Times.Once());
        }

        [Fact]
        public async Task Play_Scene_ActivatesScene()
        {
            var profile = MakeProfile();
            profile.PlaySceneId = "scene-play";

            await Execute(LightAction.Play, profile);

            _hue.Verify(h => h.ActivateSceneAsync("192.168.1.50", "testuser", "1", "scene-play", null, It.IsAny<CancellationToken>()), Times.Once);
            _sent.Should().BeEmpty();
        }

        [Fact]
        public async Task Play_TurnOff_NoTransition_SendsOffOnly()
        {
            var profile = MakeProfile();
            profile.TurnOffLightsOnPlay = true;

            await Execute(LightAction.Play, profile);

            _sent.Should().ContainSingle().Which.On.Should().BeFalse();
        }

        [Fact]
        public async Task Play_TurnOff_WithTransition_DimsThenOff()
        {
            var profile = MakeProfile();
            profile.TurnOffLightsOnPlay = true;
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 1; // 100 ms

            await Execute(LightAction.Play, profile);

            _sent.Should().HaveCount(2);
            _sent[0].Should().BeEquivalentTo(new HueLightState { On = true, Bri = 1, TransitionTime = 1 });
            _sent[1].On.Should().BeFalse();
        }

        [Fact]
        public async Task Play_TurnOff_CancelledDuringDelay_ThrowsAndSendsNoOff()
        {
            var profile = MakeProfile();
            profile.TurnOffLightsOnPlay = true;
            profile.EnablePlayTransition = true;
            profile.PlayTransitionDuration = 50; // 5 s
            using var cts = new CancellationTokenSource();
            _hue.Setup(h => h.SetGroupStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.Is<HueLightState>(s => s.Bri == 1), It.IsAny<CancellationToken>()))
                .Callback(() => cts.Cancel())
                .ReturnsAsync(true);

            var act = () => Execute(LightAction.Play, profile, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            VerifyGroupState(s => s.On == false, Times.Never());
        }

        [Fact]
        public async Task Pause_Brightness_SetsGroupState()
        {
            var profile = MakeProfile();
            profile.EnablePauseTransition = true;
            profile.PauseTransitionDuration = 3;

            await Execute(LightAction.Pause, profile);

            VerifyGroupState(s => s.On == true && s.Bri == 100 && s.TransitionTime == 3, Times.Once());
        }

        [Fact]
        public async Task Pause_Scene_ActivatesScene()
        {
            var profile = MakeProfile();
            profile.PauseSceneId = "scene-pause";

            await Execute(LightAction.Pause, profile);

            _hue.Verify(h => h.ActivateSceneAsync("192.168.1.50", "testuser", "1", "scene-pause", null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Stop_Brightness_SetsGroupState()
        {
            var profile = MakeProfile();

            await Execute(LightAction.Stop, profile);

            VerifyGroupState(s => s.On == true && s.Bri == 254 && s.TransitionTime == null, Times.Once());
        }

        [Fact]
        public async Task Stop_Scene_ActivatesScene()
        {
            var profile = MakeProfile();
            profile.StopSceneId = "scene-stop";
            profile.EnableStopTransition = true;
            profile.StopTransitionDuration = 9;

            await Execute(LightAction.Stop, profile);

            _hue.Verify(h => h.ActivateSceneAsync("192.168.1.50", "testuser", "1", "scene-stop", 9, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task BridgeUnconfigured_SendsNothing()
        {
            var bridge = new HueBridge { Id = "b", Name = "Empty", IpAddress = "", Username = "" };

            await _executor.ExecuteAsync(LightAction.Play, bridge, MakeProfile(), CancellationToken.None);

            _sent.Should().BeEmpty();
            _hue.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HueServiceThrows_IsSwallowed()
        {
            _hue.Setup(h => h.SetGroupStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("bridge offline"));

            var act = () => Execute(LightAction.Stop, MakeProfile());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task PassesTokenToHueService()
        {
            using var cts = new CancellationTokenSource();

            await Execute(LightAction.Pause, MakeProfile(), cts.Token);

            _hue.Verify(h => h.SetGroupStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<HueLightState>(), It.Is<CancellationToken>(t => t == cts.Token)), Times.Once);
        }
    }
}
