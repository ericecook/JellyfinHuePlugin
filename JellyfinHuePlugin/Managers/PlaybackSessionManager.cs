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
        private readonly LightCommandExecutor _executor;
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
            _getConfig = getConfig;
            _segmentManager = segmentManager;
            _libraryManager = libraryManager;
            _clock = timeProvider ?? TimeProvider.System;
            _executor = new LightCommandExecutor(hueService, logger, _clock);

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

            await _executor.ExecuteAsync(LightAction.Play, bridge, profile, CancellationToken.None);
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

            await _executor.ExecuteAsync(LightAction.Stop, bridge, profile, CancellationToken.None);
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
            if (!config.EnablePlugin)
            {
                // The kill switch applies to sessions already playing: no pause, resume or
                // outro commands once the plugin is disabled. Stop still restores the lights.
                return;
            }

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
                        await _executor.ExecuteAsync(LightAction.Stop, outroBridge, profile, CancellationToken.None);
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

            var bridge = ResolveBridge(config, profile);
            if (bridge == null)
            {
                _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
                return;
            }
            await _executor.ExecuteAsync(e.IsPaused ? LightAction.Pause : LightAction.Play, bridge, profile, CancellationToken.None);
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
