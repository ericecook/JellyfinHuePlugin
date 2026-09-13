using System;
using System.Collections.Generic;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    /// <summary>
    /// SessionPolicy is pure, so these tests need no mocks: build a snapshot, feed an
    /// input, check the decision, the action and the returned state.
    /// </summary>
    public class SessionPolicyTests
    {
        private static readonly DateTimeOffset StartedAt = new(2026, 9, 12, 20, 0, 0, TimeSpan.Zero);
        private static readonly IReadOnlyList<TickRange> OutroSegments = new[] { new TickRange(100 * TimeSpan.TicksPerSecond, 110 * TimeSpan.TicksPerSecond) };

        private static LightControlProfile Profile(int grace = 0, bool outro = false) => new()
        {
            Name = "Test Profile",
            PauseGracePeriodSeconds = grace,
            EnableOutroLights = outro
        };

        private static SessionSnapshot Snapshot(LightControlProfile profile, PlaybackState state = PlaybackState.Playing, bool outroTriggered = false) =>
            new(profile, StartedAt, OutroSegments, state, outroTriggered);

        private static ProgressInput Input(bool paused = false, long positionSeconds = 0, int elapsedSeconds = 60, bool pluginEnabled = true) =>
            new(paused, positionSeconds * TimeSpan.TicksPerSecond, pluginEnabled, StartedAt.AddSeconds(elapsedSeconds));

        [Fact]
        public void PluginDisabled_BeatsOutro()
        {
            var state = Snapshot(Profile(outro: true));

            var result = SessionPolicy.OnProgress(state, Input(positionSeconds: 105, pluginEnabled: false));

            result.Decision.Should().Be(ProgressDecision.PluginDisabled);
            result.State.OutroLightsTriggered.Should().BeFalse();
            ReferenceEquals(result.State, state).Should().BeTrue();
        }

        [Fact]
        public void PluginDisabled_NoActionAndSameInstance()
        {
            var state = Snapshot(Profile());

            var result = SessionPolicy.OnProgress(state, Input(paused: true, pluginEnabled: false));

            result.Decision.Should().Be(ProgressDecision.PluginDisabled);
            result.Action.Should().BeNull();
            ReferenceEquals(result.State, state).Should().BeTrue();
        }

        [Theory]
        [InlineData(PlaybackState.Playing, false)]
        [InlineData(PlaybackState.Paused, true)]
        public void SameState_None(PlaybackState current, bool paused)
        {
            var state = Snapshot(Profile(), current);

            var result = SessionPolicy.OnProgress(state, Input(paused: paused));

            result.Decision.Should().Be(ProgressDecision.None);
            result.Action.Should().BeNull();
            ReferenceEquals(result.State, state).Should().BeTrue();
        }

        [Fact]
        public void Pause_ReturnsPauseActionAndPausedState()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile()), Input(paused: true));

            result.Decision.Should().Be(ProgressDecision.Pause);
            result.Action.Should().Be(LightAction.Pause);
            result.State.PlaybackState.Should().Be(PlaybackState.Paused);
        }

        [Fact]
        public void Resume_ReturnsPlayActionAndPlayingState()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(), PlaybackState.Paused), Input(paused: false));

            result.Decision.Should().Be(ProgressDecision.Resume);
            result.Action.Should().Be(LightAction.Play);
            result.State.PlaybackState.Should().Be(PlaybackState.Playing);
        }

        // ProgressDecision is internal, so the expectation is a bool: xunit theory parameters
        // must be accessible from the public test method.
        [Theory]
        [InlineData(0, true)]
        [InlineData(29, true)]
        [InlineData(30, false)]
        [InlineData(31, false)]
        public void PauseGrace_BoundariesInclusive(int elapsedSeconds, bool expectIgnored)
        {
            var state = Snapshot(Profile(grace: 30));

            var result = SessionPolicy.OnProgress(state, Input(paused: true, elapsedSeconds: elapsedSeconds));

            if (expectIgnored)
            {
                result.Decision.Should().Be(ProgressDecision.GraceIgnored);
                result.Action.Should().BeNull();
                ReferenceEquals(result.State, state).Should().BeTrue();
            }
            else
            {
                result.Decision.Should().Be(ProgressDecision.Pause);
                result.Action.Should().Be(LightAction.Pause);
                result.State.PlaybackState.Should().Be(PlaybackState.Paused);
            }
        }

        [Fact]
        public void GraceZero_PausesImmediately()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(grace: 0)), Input(paused: true, elapsedSeconds: 0));

            result.Decision.Should().Be(ProgressDecision.Pause);
        }

        [Fact]
        public void ResumeDuringGrace_IsNotGated()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(grace: 30), PlaybackState.Paused), Input(paused: false, elapsedSeconds: 5));

            result.Decision.Should().Be(ProgressDecision.Resume);
            result.Action.Should().Be(LightAction.Play);
        }

        [Fact]
        public void InOutro_ReturnsStopAndMarksTriggered()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(outro: true)), Input(positionSeconds: 105));

            result.Decision.Should().Be(ProgressDecision.Outro);
            result.Action.Should().Be(LightAction.Stop);
            result.State.OutroLightsTriggered.Should().BeTrue();
            result.State.PlaybackState.Should().Be(PlaybackState.Playing);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AfterOutroTriggered_FrozenForPauseAndResume(bool paused)
        {
            var state = Snapshot(Profile(outro: true), paused ? PlaybackState.Playing : PlaybackState.Paused, outroTriggered: true);

            var result = SessionPolicy.OnProgress(state, Input(paused: paused, positionSeconds: 120));

            result.Decision.Should().Be(ProgressDecision.FrozenAfterOutro);
            result.Action.Should().BeNull();
            ReferenceEquals(result.State, state).Should().BeTrue();
        }

        [Fact]
        public void OutroDisabled_IgnoresSegments()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(outro: false)), Input(paused: true, positionSeconds: 105));

            result.Decision.Should().Be(ProgressDecision.Pause);
            result.State.OutroLightsTriggered.Should().BeFalse();
        }

        [Fact]
        public void OutroBeatsPause()
        {
            var result = SessionPolicy.OnProgress(Snapshot(Profile(outro: true)), Input(paused: true, positionSeconds: 105));

            result.Decision.Should().Be(ProgressDecision.Outro);
            result.State.PlaybackState.Should().Be(PlaybackState.Playing);
        }

        [Theory]
        [InlineData(99, false)]
        [InlineData(100, true)]
        [InlineData(110, true)]
        [InlineData(111, false)]
        public void IsInOutro_BoundariesInclusive(long positionTicks, bool expected)
        {
            var segments = new[] { new TickRange(100, 110) };

            SessionPolicy.IsInOutro(segments, positionTicks).Should().Be(expected);
        }

        [Fact]
        public void IsInOutro_EmptyList_IsFalse()
        {
            SessionPolicy.IsInOutro(Array.Empty<TickRange>(), 100).Should().BeFalse();
        }
    }
}
