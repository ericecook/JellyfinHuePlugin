using System;
using System.Collections.Generic;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Managers
{
    /// <summary>Inclusive tick range of a media segment.</summary>
    internal readonly record struct TickRange(long StartTicks, long EndTicks);

    public enum PlaybackState
    {
        Playing,
        Paused,
        Stopped
    }

    /// <summary>Immutable per-session state. Every change produces a new instance.</summary>
    internal sealed record SessionSnapshot(
        LightControlProfile Profile,
        DateTimeOffset StartedAt,
        IReadOnlyList<TickRange> OutroSegments,
        PlaybackState PlaybackState,
        bool OutroLightsTriggered)
    {
        /// <summary>The item playing when the snapshot was taken; null when Jellyfin gave none. A stop for another item is ignored.</summary>
        public Guid? ItemId { get; init; }
    }

    /// <summary>Everything a progress event contributes to the decision.</summary>
    internal readonly record struct ProgressInput(
        bool IsPaused,
        long PositionTicks,
        bool PluginEnabled,
        DateTimeOffset Now);

    internal enum ProgressDecision
    {
        /// <summary>Nothing changed.</summary>
        None,
        /// <summary>EnablePlugin is false: no pause, resume or outro for in-flight sessions.</summary>
        PluginDisabled,
        /// <summary>The outro already raised the lights; leave them until stop.</summary>
        FrozenAfterOutro,
        /// <summary>Position entered an outro segment: stop lights, mark triggered.</summary>
        Outro,
        /// <summary>Pause inside PauseGracePeriodSeconds since StartedAt.</summary>
        GraceIgnored,
        Pause,
        Resume
    }

    internal sealed record ProgressResult(SessionSnapshot State, ProgressDecision Decision, LightAction? Action);

    /// <summary>
    /// Decides what a progress event does. Pure: no logging, no clock, no configuration
    /// lookups. The returned State is the input instance whenever nothing changed.
    /// </summary>
    internal static class SessionPolicy
    {
        internal static ProgressResult OnProgress(SessionSnapshot state, ProgressInput input)
        {
            if (!input.PluginEnabled)
            {
                return new ProgressResult(state, ProgressDecision.PluginDisabled, null);
            }

            if (state.Profile.EnableOutroLights)
            {
                if (state.OutroLightsTriggered)
                {
                    return new ProgressResult(state, ProgressDecision.FrozenAfterOutro, null);
                }

                if (IsInOutro(state.OutroSegments, input.PositionTicks))
                {
                    return new ProgressResult(state with { OutroLightsTriggered = true }, ProgressDecision.Outro, LightAction.Stop);
                }
            }

            var current = input.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
            if (current == state.PlaybackState)
            {
                return new ProgressResult(state, ProgressDecision.None, null);
            }

            if (input.IsPaused
                && state.Profile.PauseGracePeriodSeconds > 0
                && (input.Now - state.StartedAt).TotalSeconds < state.Profile.PauseGracePeriodSeconds)
            {
                // The playback state stays Playing, so a pause held past the window fires on the next report.
                return new ProgressResult(state, ProgressDecision.GraceIgnored, null);
            }

            var next = state with { PlaybackState = current };
            return current == PlaybackState.Paused
                ? new ProgressResult(next, ProgressDecision.Pause, LightAction.Pause)
                : new ProgressResult(next, ProgressDecision.Resume, LightAction.Play);
        }

        /// <summary>True when the position lies inside any segment (bounds inclusive).</summary>
        internal static bool IsInOutro(IReadOnlyList<TickRange> segments, long positionTicks)
        {
            foreach (var segment in segments)
            {
                if (positionTicks >= segment.StartTicks && positionTicks <= segment.EndTicks)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
