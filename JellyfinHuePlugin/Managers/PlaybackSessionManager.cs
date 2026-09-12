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
using MediaBrowser.Model.Session;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Managers
{
    public class PlaybackSessionManager : IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackSessionManager> _logger;
        private readonly HueService _hueService;
        private readonly Func<PluginConfiguration> _getConfig;
        private readonly IMediaSegmentManager _segmentManager;
        private readonly ILibraryManager _libraryManager;

        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

        private class SessionState
        {
            public string PlaybackState { get; set; } = "Playing";
            public LightControlProfile? Profile { get; set; }
            public bool OutroLightsTriggered { get; set; }
        }

        public PlaybackSessionManager(
            ISessionManager sessionManager,
            ILogger<PlaybackSessionManager> logger,
            HueService hueService,
            Func<PluginConfiguration> getConfig,
            IMediaSegmentManager segmentManager,
            ILibraryManager libraryManager)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _hueService = hueService;
            _getConfig = getConfig;
            _segmentManager = segmentManager;
            _libraryManager = libraryManager;

            // Subscribe to session events
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
        }

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try { await OnPlaybackStartAsync(e); }
            catch (Exception ex) { _logger.LogError(ex, "Error handling playback start"); }
        }

        private async Task OnPlaybackStartAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            var config = _getConfig();

            var mediaType = e.Item?.GetType().Name ?? e.MediaSourceId ?? "Unknown";
            var isMovie = e.Item?.GetType().Name == "Movie" || e.MediaInfo?.Container == "Movie";
            var isEpisode = e.Item?.GetType().Name == "Episode" || e.MediaInfo?.Container == "Episode";

            var profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);

            if (profile == null)
            {
                return;
            }

            _logger.LogInformation("Playback started on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}, Type: {MediaType}) - Using profile: {ProfileName}",
                e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, mediaType, profile.Name);

            _sessions[e.Session.Id] = new SessionState
            {
                PlaybackState = "Playing",
                Profile = profile,
                OutroLightsTriggered = false
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

        private async Task OnPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            if (e.Session == null) return;

            var config = _getConfig();

            var isMovie = e.Item?.GetType().Name == "Movie";
            var isEpisode = e.Item?.GetType().Name == "Episode";

            var profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);

            if (profile == null)
            {
                return;
            }

            _logger.LogInformation("Playback stopped on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);

            _sessions.TryRemove(e.Session.Id, out _);

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

        private async Task OnPlaybackProgressAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            var config = _getConfig();

            var isMovie = e.Item?.GetType().Name == "Movie";
            var isEpisode = e.Item?.GetType().Name == "Episode";

            var profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);

            if (profile == null)
            {
                return;
            }

            if (!_sessions.TryGetValue(e.Session.Id, out var session))
            {
                return;
            }

            // Check for outro segment if enabled and not already triggered
            if (profile.EnableOutroLights && !session.OutroLightsTriggered)
            {
                if (await IsInOutroSegmentAsync(e))
                {
                    _logger.LogInformation("[{ProfileName}] Outro segment detected - triggering stop lights on {ClientName}",
                        profile.Name, e.ClientName);
                    session.OutroLightsTriggered = true;
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

            if (session.PlaybackState != currentState)
            {
                // Skip pause action during grace period at the start of playback
                if (e.IsPaused && profile.PauseGracePeriodSeconds > 0)
                {
                    var positionTicks = e.PlaybackPositionTicks ?? 0;
                    var positionSeconds = positionTicks / TimeSpan.TicksPerSecond;
                    if (positionSeconds < profile.PauseGracePeriodSeconds)
                    {
                        _logger.LogInformation("[{ProfileName}] Pause ignored — within grace period ({PositionSeconds}s < {GracePeriod}s) on {ClientName}",
                            profile.Name, positionSeconds, profile.PauseGracePeriodSeconds, e.ClientName);
                        return;
                    }
                }

                _logger.LogInformation("Playback state changed to {State} on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                    currentState, e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);
                session.PlaybackState = currentState;
                session.Profile = profile;

                var state = e.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
                var bridge = ResolveBridge(config, profile);
                if (bridge == null)
                {
                    _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
                    return;
                }
                await HandlePlaybackStateAsync(state, bridge, profile);
            }
        }

        private async Task<bool> IsInOutroSegmentAsync(PlaybackProgressEventArgs e)
        {
            var item = e.Item;
            if (item == null)
            {
                return false;
            }

            var positionTicks = e.PlaybackPositionTicks ?? 0;

            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var segments = await _segmentManager.GetSegmentsAsync(item,
                    new[] { MediaSegmentType.Outro }, libraryOptions, filterByProvider: false);

                foreach (var segment in segments)
                {
                    if (positionTicks >= segment.StartTicks && positionTicks <= segment.EndTicks)
                    {
                        _logger.LogInformation("Outro segment detected at position {Position}s (segment: {Start}s - {End}s)",
                            positionTicks / 10000000.0, segment.StartTicks / 10000000.0, segment.EndTicks / 10000000.0);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outro detection: Error querying media segments");
            }

            return false;
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
