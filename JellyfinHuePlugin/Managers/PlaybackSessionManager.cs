using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Session;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Managers
{
    /// <summary>Inclusive tick range of a media segment.</summary>
    internal readonly record struct TickRange(long StartTicks, long EndTicks);

    public class PlaybackSessionManager : IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackSessionManager> _logger;
        private readonly HueService _hueService;
        private readonly Func<PluginConfiguration> _getConfig;
        private readonly IMediaSegmentManager _segmentManager;
        private readonly ILibraryManager _libraryManager;
        private readonly TimeProvider _clock;

        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

        private sealed class SessionState
        {
            public required LightControlProfile Profile { get; init; }
            public required DateTimeOffset StartedAt { get; init; }
            public required IReadOnlyList<TickRange> OutroSegments { get; init; }
            public string PlaybackState { get; set; } = "Playing";
            public bool OutroLightsTriggered { get; set; }
        }

        public PlaybackSessionManager(
            ISessionManager sessionManager,
            ILogger<PlaybackSessionManager> logger,
            HueService hueService,
            Func<PluginConfiguration> getConfig,
            IMediaSegmentManager segmentManager,
            ILibraryManager libraryManager,
            TimeProvider? timeProvider = null)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _hueService = hueService;
            _getConfig = getConfig;
            _segmentManager = segmentManager;
            _libraryManager = libraryManager;
            _clock = timeProvider ?? TimeProvider.System;

            // Subscribe to session events
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
        }

        /// <summary>Movie/episode detection by entity type. Anything else, including null, is neither.</summary>
        internal static (bool IsMovie, bool IsEpisode) ClassifyItem(BaseItem? item)
            => (item is Movie, item is Episode);

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try { await OnPlaybackStartAsync(e); }
            catch (Exception ex) { _logger.LogError(ex, "Error handling playback start"); }
        }

        internal async Task OnPlaybackStartAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            var config = _getConfig();

            var (isMovie, isEpisode) = ClassifyItem(e.Item);
            var mediaType = e.Item?.GetType().Name ?? "Unknown";

            var profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);

            if (profile == null)
            {
                // A profile stored for an earlier item on this session must not drive lights for this one.
                _sessions.TryRemove(e.Session.Id, out _);
                return;
            }

            _logger.LogInformation("Playback started on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}, Type: {MediaType}) - Using profile: {ProfileName}",
                e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, mediaType, profile.Name);

            var outroSegments = profile.EnableOutroLights
                ? await LoadOutroSegmentsAsync(e.Item)
                : Array.Empty<TickRange>();

            _sessions[e.Session.Id] = new SessionState
            {
                Profile = profile,
                StartedAt = _clock.GetUtcNow(),
                OutroSegments = outroSegments,
                PlaybackState = "Playing"
            };

            var bridge = ResolveBridge(config, profile);
            if (bridge == null)
            {
                _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
                return;
            }

            await HandlePlaybackStateAsync(PlaybackState.Playing, bridge, profile);
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try { await OnPlaybackStoppedAsync(e); }
            catch (Exception ex) { _logger.LogError(ex, "Error handling playback stop"); }
        }

        internal async Task OnPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            if (e.Session == null) return;

            var config = _getConfig();

            _sessions.TryRemove(e.Session.Id, out var state);
            var profile = state?.Profile;
            if (profile == null)
            {
                // No recorded start (for example the server restarted mid-playback):
                // match afresh so the lights are still restored.
                var (isMovie, isEpisode) = ClassifyItem(e.Item);
                profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);
            }

            if (profile == null)
            {
                return;
            }

            _logger.LogInformation("Playback stopped on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);

            var bridge = ResolveBridge(config, profile);
            if (bridge == null)
            {
                _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
                return;
            }

            await HandlePlaybackStateAsync(PlaybackState.Stopped, bridge, profile);
        }

        private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            try { await OnPlaybackProgressAsync(e); }
            catch (Exception ex) { _logger.LogError(ex, "Error handling playback progress"); }
        }

        internal async Task OnPlaybackProgressAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            if (!_sessions.TryGetValue(e.Session.Id, out var state))
            {
                return;
            }

            var config = _getConfig();
            var profile = state.Profile;

            if (profile.EnableOutroLights)
            {
                if (state.OutroLightsTriggered)
                {
                    // Credits: the outro already raised the lights; pause and resume leave them alone until stop.
                    return;
                }

                var positionTicks = e.PlaybackPositionTicks ?? 0;
                if (IsInOutro(state.OutroSegments, positionTicks))
                {
                    state.OutroLightsTriggered = true;
                    _logger.LogInformation("[{ProfileName}] Outro segment detected at {Position:F1}s - triggering stop lights on {ClientName}",
                        profile.Name, positionTicks / (double)TimeSpan.TicksPerSecond, e.ClientName);
                    var outroBridge = ResolveBridge(config, profile);
                    if (outroBridge != null)
                    {
                        await HandlePlaybackStateAsync(PlaybackState.Stopped, outroBridge, profile);
                    }
                    return;
                }
            }

            // Detect pause/unpause
            var currentState = e.IsPaused ? "Paused" : "Playing";
            if (state.PlaybackState == currentState)
            {
                return;
            }

            // Skip pause action during the grace period after playback started
            if (e.IsPaused && profile.PauseGracePeriodSeconds > 0)
            {
                var elapsed = _clock.GetUtcNow() - state.StartedAt;
                if (elapsed.TotalSeconds < profile.PauseGracePeriodSeconds)
                {
                    _logger.LogInformation("[{ProfileName}] Pause ignored — within grace period ({ElapsedSeconds}s < {GracePeriod}s) on {ClientName}",
                        profile.Name, (int)elapsed.TotalSeconds, profile.PauseGracePeriodSeconds, e.ClientName);
                    return;
                }
            }

            _logger.LogInformation("Playback state changed to {State} on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                currentState, e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);
            state.PlaybackState = currentState;

            var newState = e.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
            var bridge = ResolveBridge(config, profile);
            if (bridge == null)
            {
                _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
                return;
            }
            await HandlePlaybackStateAsync(newState, bridge, profile);
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

        /// <summary>
        /// Outro segments for the item, fetched once per playback. A null item or a failed
        /// query yields an empty list; the failure is logged once.
        /// </summary>
        private async Task<IReadOnlyList<TickRange>> LoadOutroSegmentsAsync(BaseItem? item)
        {
            if (item == null)
            {
                return Array.Empty<TickRange>();
            }

            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var segments = await _segmentManager.GetSegmentsAsync(item,
                    new[] { MediaSegmentType.Outro }, libraryOptions, filterByProvider: false);
                return segments.Select(s => new TickRange(s.StartTicks, s.EndTicks)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outro detection: Error querying media segments");
                return Array.Empty<TickRange>();
            }
        }
        
        // Matching itself is pure (ProfileMatcher); this wraps it with the Debug
        // diagnostics that TROUBLESHOOTING.md points users at.
        private LightControlProfile? TryMatchProfile(string? clientName, string? deviceId, string? remoteEndpoint, bool isMovie, bool isEpisode, PluginConfiguration config)
        {
            var request = new MatchRequest(clientName ?? string.Empty, deviceId ?? string.Empty, remoteEndpoint ?? string.Empty, isMovie, isEpisode);
            var result = ProfileMatcher.FindMatchingProfile(config, request);

            foreach (var rejection in result.Rejections)
            {
                _logger.LogDebug("[{ProfileName}] {Filter} filter rejected: {Actual} vs {Expected}",
                    rejection.ProfileName, rejection.Filter, rejection.Actual, rejection.Expected);
            }

            switch (result.Status)
            {
                case MatchStatus.Matched:
                    _logger.LogDebug("Matched profile: {ProfileName}", result.Profile!.Name);
                    break;
                case MatchStatus.NoMatch:
                    _logger.LogDebug("No profiles matched for {ClientName} (Device: {DeviceId}, IP: {IP})",
                        request.ClientName, request.DeviceId, ProfileMatcher.ExtractIpAddress(request.RemoteEndpoint));
                    break;
                case MatchStatus.NoProfiles:
                    _logger.LogDebug("No profiles configured");
                    break;
                case MatchStatus.PluginDisabled:
                    // Nothing is logged when the plugin is disabled, as before.
                    break;
            }

            return result.Profile;
        }

        private HueBridge? ResolveBridge(PluginConfiguration config, LightControlProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.BridgeId))
            {
                return config.Bridges.Count == 1 ? config.Bridges[0] : null;
            }

            return config.Bridges.FirstOrDefault(b => b.Id == profile.BridgeId);
        }

        private async Task HandlePlaybackStateAsync(PlaybackState state, HueBridge bridge, LightControlProfile profile)
        {
            if (string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                _logger.LogWarning("Bridge {BridgeName} not fully configured", bridge.Name);
                return;
            }

            try
            {
                switch (state)
                {
                    case PlaybackState.Playing:
                        await HandlePlayingStateAsync(bridge, profile);
                        break;
                    case PlaybackState.Paused:
                        await HandlePausedStateAsync(bridge, profile);
                        break;
                    case PlaybackState.Stopped:
                        await HandleStoppedStateAsync(bridge, profile);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling Hue lights for state {State}", state);
            }
        }

        private async Task HandlePlayingStateAsync(HueBridge bridge, LightControlProfile profile)
        {
            int? transitionTime = profile.EnablePlayTransition ? profile.PlayTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.PlaySceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating play scene {SceneId}", profile.Name, profile.PlaySceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.PlaySceneId,
                    transitionTime);
            }
            else if (profile.TurnOffLightsOnPlay)
            {
                _logger.LogInformation("[{ProfileName}] Turning off lights in group {GroupId}", profile.Name, profile.TargetGroupId);
                if (transitionTime.HasValue && transitionTime.Value > 0)
                {
                    // Dim to minimum first so the transition is visible, then turn off
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = true, Bri = 1, TransitionTime = transitionTime });
                    // Wait for the transition to complete (transitionTime is in deciseconds; ×100 = milliseconds)
                    await Task.Delay(transitionTime.Value * 100);
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = false });
                }
                else
                {
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = false });
                }
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Dimming lights to {Brightness}", profile.Name, profile.PlayBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PlayBrightness, TransitionTime = transitionTime });
            }
        }

        private async Task HandlePausedStateAsync(HueBridge bridge, LightControlProfile profile)
        {
            int? transitionTime = profile.EnablePauseTransition ? profile.PauseTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.PauseSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating pause scene {SceneId}", profile.Name, profile.PauseSceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.PauseSceneId,
                    transitionTime);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Brightening lights to {Brightness}", profile.Name, profile.PauseBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PauseBrightness, TransitionTime = transitionTime });
            }
        }

        private async Task HandleStoppedStateAsync(HueBridge bridge, LightControlProfile profile)
        {
            int? transitionTime = profile.EnableStopTransition ? profile.StopTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.StopSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating stop scene {SceneId}", profile.Name, profile.StopSceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.StopSceneId,
                    transitionTime);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Turning lights on to {Brightness}", profile.Name, profile.StopBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.StopBrightness, TransitionTime = transitionTime });
            }
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        }
    }

    public enum PlaybackState
    {
        Playing,
        Paused,
        Stopped
    }
}
