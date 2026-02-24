using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
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
        
        private readonly Dictionary<string, SessionState> _sessions = new Dictionary<string, SessionState>();

        private class SessionState
        {
            public string PlaybackState { get; set; } = "Playing";
            public LightControlProfile? Profile { get; set; }
            public bool OutroLightsTriggered { get; set; }
        }
        
        // Cache for segment data to avoid repeated database queries
        private readonly Dictionary<Guid, List<MediaSegmentCache>> _segmentCache = new Dictionary<Guid, List<MediaSegmentCache>>();

        // Cache for IntroSkipper reflection to avoid scanning assemblies on every progress tick
        private bool _introSkipperResolved;
        private System.Reflection.MethodInfo? _introSkipperDbPathGetter;
        private System.Reflection.PropertyInfo? _introSkipperInstanceProp;
        private Type? _introSkipperPluginType;

        public PlaybackSessionManager(
            ISessionManager sessionManager,
            ILogger<PlaybackSessionManager> logger,
            HueService hueService,
            Func<PluginConfiguration> getConfig)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _hueService = hueService;
            _getConfig = getConfig;

            // Subscribe to session events
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
        }

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                var config = _getConfig();

                var mediaType = e.Item?.GetType().Name ?? e.MediaSourceId ?? "Unknown";
                var isMovie = e.Item?.GetType().Name == "Movie" || e.MediaInfo?.Container == "Movie";
                var isEpisode = e.Item?.GetType().Name == "Episode" || e.MediaInfo?.Container == "Episode";

                var profile = FindMatchingProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint ?? "", isMovie, isEpisode, config);

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

                await HandlePlaybackStateAsync(PlaybackState.Playing, config, profile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling playback start");
            }
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                var config = _getConfig();

                var isMovie = e.Item?.GetType().Name == "Movie";
                var isEpisode = e.Item?.GetType().Name == "Episode";

                var profile = FindMatchingProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint ?? "", isMovie, isEpisode, config);

                if (profile == null)
                {
                    return;
                }

                _logger.LogInformation("Playback stopped on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                    e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);

                _sessions.Remove(e.Session.Id);

                // Clear segment cache for this item to free memory
                if (e.Item != null)
                {
                    _segmentCache.Remove(e.Item.Id);
                }

                await HandlePlaybackStateAsync(PlaybackState.Stopped, config, profile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling playback stop");
            }
        }

        private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                var config = _getConfig();

                var isMovie = e.Item?.GetType().Name == "Movie";
                var isEpisode = e.Item?.GetType().Name == "Episode";

                var profile = FindMatchingProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint ?? "", isMovie, isEpisode, config);

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
                    if (IsInOutroSegment(e))
                    {
                        _logger.LogInformation("[{ProfileName}] Outro segment detected - triggering stop lights on {ClientName}",
                            profile.Name, e.ClientName);
                        session.OutroLightsTriggered = true;
                        await HandlePlaybackStateAsync(PlaybackState.Stopped, config, profile);
                        return;
                    }
                }

                // Detect pause/unpause
                var currentState = e.IsPaused ? "Paused" : "Playing";

                if (session.PlaybackState != currentState)
                {
                    _logger.LogInformation("Playback state changed to {State} on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                        currentState, e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);
                    session.PlaybackState = currentState;
                    session.Profile = profile;

                    var state = e.IsPaused ? PlaybackState.Paused : PlaybackState.Playing;
                    await HandlePlaybackStateAsync(state, config, profile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling playback progress");
            }
        }

        private bool IsInOutroSegment(PlaybackProgressEventArgs e)
        {
            var item = e.Item;
            if (item == null)
            {
                return false;
            }

            var positionTicks = e.PlaybackPositionTicks ?? 0;

            try
            {
                var dbPath = GetIntroSkipperDbPath();
                if (dbPath == null)
                {
                    return false;
                }

                return QueryDatabaseForSegments(dbPath, item.Id, positionTicks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outro detection: Error accessing IntroSkipper data");
            }

            return false;
        }

        private string? GetIntroSkipperDbPath()
        {
            if (!_introSkipperResolved)
            {
                _introSkipperResolved = true;
                try
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.FullName?.Contains("IntroSkipper", StringComparison.OrdinalIgnoreCase) == true);

                    if (assembly == null)
                    {
                        _logger.LogDebug("Outro detection: IntroSkipper plugin not found");
                        return null;
                    }

                    _introSkipperPluginType = assembly.GetTypes().FirstOrDefault(t => t.Name == "Plugin");
                    if (_introSkipperPluginType == null) return null;

                    _introSkipperInstanceProp = _introSkipperPluginType.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                    _introSkipperDbPathGetter = _introSkipperPluginType
                        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "get_DbPath");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Outro detection: Error resolving IntroSkipper reflection");
                }
            }

            if (_introSkipperInstanceProp == null || _introSkipperDbPathGetter == null)
            {
                return null;
            }

            var instance = _introSkipperInstanceProp.GetValue(null);
            return instance != null ? _introSkipperDbPathGetter.Invoke(instance, null)?.ToString() : null;
        }
        
        // Simple cache class for segment data
        private class MediaSegmentCache
        {
            public string Type { get; set; } = string.Empty;
            public long StartTicks { get; set; }
            public long EndTicks { get; set; }
        }
        
        private bool QueryDatabaseForSegments(string dbPath, Guid itemId, long positionTicks)
        {
            try
            {
                // Check cache first
                if (_segmentCache.TryGetValue(itemId, out var cachedSegments))
                {
                    return CheckCachedSegments(cachedSegments, positionTicks);
                }
                
                // Not in cache, query database
                if (!System.IO.File.Exists(dbPath))
                {
                    _logger.LogWarning("Outro detection: IntroSkipper database not found at {DbPath}", dbPath);
                    return false;
                }
                
                var connectionString = $"Data Source={dbPath};Mode=ReadOnly";
                var segments = new List<MediaSegmentCache>();
                
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    
                    var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT ItemId, Type, Start, End 
                        FROM DbSegment 
                        WHERE ItemId = $itemId";
                    
                    // IntroSkipper stores ItemIds in UPPERCASE format
                    var itemIdString = itemId.ToString().ToUpperInvariant();
                    command.Parameters.AddWithValue("$itemId", itemIdString);
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var type = reader.GetString(1);
                            var start = reader.GetDouble(2);
                            var end = reader.GetDouble(3);
                            
                            // Convert seconds to ticks and store in cache
                            segments.Add(new MediaSegmentCache
                            {
                                Type = type,
                                StartTicks = (long)(start * 10000000),
                                EndTicks = (long)(end * 10000000)
                            });
                        }
                    }
                }
                
                // Store in cache
                _segmentCache[itemId] = segments;
                
                _logger.LogDebug("Outro detection: Cached {Count} segments for item", segments.Count);
                
                // Check the newly cached segments
                return CheckCachedSegments(segments, positionTicks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outro detection: Error querying IntroSkipper database");
            }
            
            return false;
        }
        
        private bool CheckCachedSegments(List<MediaSegmentCache> segments, long positionTicks)
        {
            foreach (var segment in segments)
            {
                // IntroSkipper uses numeric types: 0 = Intro, 1 = Credits (Outro)
                var isOutro = segment.Type == "1" || 
                             segment.Type.Equals("Outro", StringComparison.OrdinalIgnoreCase) ||
                             segment.Type.Equals("Credits", StringComparison.OrdinalIgnoreCase);
                
                if (isOutro)
                {
                    if (positionTicks >= segment.StartTicks && positionTicks <= segment.EndTicks)
                    {
                        _logger.LogInformation("Outro segment detected at position {Position}s (segment: {Start}s - {End}s)",
                            positionTicks / 10000000.0, segment.StartTicks / 10000000.0, segment.EndTicks / 10000000.0);
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        private LightControlProfile? FindMatchingProfile(string clientName, string deviceId, string remoteEndpoint, bool isMovie, bool isEpisode, PluginConfiguration config)
        {
            if (!config.EnablePlugin)
            {
                return null;
            }

            // Try to match against defined profiles
            if (config.Profiles != null && config.Profiles.Count > 0)
            {
                foreach (var profile in config.Profiles)
                {
                    // Check media type filter first
                    if (!MediaTypeMatches(profile, isMovie, isEpisode))
                    {
                        _logger.LogDebug("[{ProfileName}] Media type mismatch (Movie={IsMovie}, Episode={IsEpisode}, EnableMovies={EnableMovies}, EnableTv={EnableTv})", 
                            profile.Name, isMovie, isEpisode, profile.EnableForMovies, profile.EnableForTvShows);
                        continue;
                    }
                    
                    if (ProfileMatches(profile, clientName, deviceId, remoteEndpoint))
                    {
                        _logger.LogDebug("Matched profile: {ProfileName}", profile.Name);
                        return profile;
                    }
                }
                
                // Profiles defined but none matched
                _logger.LogDebug("No profiles matched for {ClientName} (Device: {DeviceId}, IP: {IP})", 
                    clientName, deviceId, ExtractIpAddress(remoteEndpoint));
            }
            else
            {
                _logger.LogDebug("No profiles configured");
            }

            return null;
        }

        private bool MediaTypeMatches(LightControlProfile profile, bool isMovie, bool isEpisode)
        {
            // If it's a movie, check if movies are enabled
            if (isMovie)
            {
                return profile.EnableForMovies;
            }
            
            // If it's an episode/TV show, check if TV shows are enabled
            if (isEpisode)
            {
                return profile.EnableForTvShows;
            }
            
            // Unknown media type - don't match
            return false;
        }

        private bool ProfileMatches(LightControlProfile profile, string clientName, string deviceId, string remoteEndpoint)
        {
            bool hasClientFilter = !string.IsNullOrWhiteSpace(profile.TargetClientName);
            bool hasDeviceFilter = profile.TargetDeviceIds != null && profile.TargetDeviceIds.Count > 0;
            bool hasIpFilter = !string.IsNullOrWhiteSpace(profile.TargetIpAddress);
            
            // If no filters in profile, it matches everything
            if (!hasClientFilter && !hasDeviceFilter && !hasIpFilter)
            {
                return true;
            }

            // Check IP address (most specific)
            if (hasIpFilter)
            {
                var clientIp = ExtractIpAddress(remoteEndpoint);
                bool ipMatches = clientIp.Equals(profile.TargetIpAddress, StringComparison.OrdinalIgnoreCase);
                _logger.LogDebug("[{ProfileName}] IP address filter check: {ClientIp} vs {TargetIp} = {Match}", 
                    profile.Name, clientIp, profile.TargetIpAddress, ipMatches);
                
                if (!ipMatches)
                {
                    return false;
                }
            }
            
            // Check Device ID list
            if (hasDeviceFilter)
            {
                bool deviceMatches = profile.TargetDeviceIds?.Any(id => 
                    deviceId.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? false;
                _logger.LogDebug("[{ProfileName}] DeviceId filter check: {DeviceId} in [{TargetDeviceIds}] = {Match}", 
                    profile.Name, deviceId, string.Join(", ", profile.TargetDeviceIds ?? new List<string>()), deviceMatches);
                
                if (!deviceMatches)
                {
                    return false;
                }
            }
            
            // Check ClientName (least specific, substring match)
            if (hasClientFilter)
            {
                bool clientMatches = clientName.Contains(profile.TargetClientName, StringComparison.OrdinalIgnoreCase);
                _logger.LogDebug("[{ProfileName}] ClientName filter check: {ClientName} contains {TargetClientName} = {Match}", 
                    profile.Name, clientName, profile.TargetClientName, clientMatches);
                
                if (!clientMatches)
                {
                    return false;
                }
            }

            return true;
        }
        
        private string ExtractIpAddress(string remoteEndpoint)
        {
            if (string.IsNullOrWhiteSpace(remoteEndpoint))
            {
                return string.Empty;
            }
            
            // Remote endpoint format is usually "IP:PORT" or just "IP"
            var colonIndex = remoteEndpoint.IndexOf(':');
            if (colonIndex > 0)
            {
                return remoteEndpoint.Substring(0, colonIndex);
            }
            
            return remoteEndpoint;
        }

        private async Task HandlePlaybackStateAsync(PlaybackState state, PluginConfiguration config, LightControlProfile profile)
        {
            if (string.IsNullOrWhiteSpace(config.BridgeIpAddress) || string.IsNullOrWhiteSpace(config.Username))
            {
                _logger.LogWarning("Hue bridge not configured");
                return;
            }

            try
            {
                switch (state)
                {
                    case PlaybackState.Playing:
                        await HandlePlayingStateAsync(config, profile);
                        break;
                    case PlaybackState.Paused:
                        await HandlePausedStateAsync(config, profile);
                        break;
                    case PlaybackState.Stopped:
                        await HandleStoppedStateAsync(config, profile);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling Hue lights for state {State}", state);
            }
        }

        private async Task HandlePlayingStateAsync(PluginConfiguration config, LightControlProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.PlaySceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating play scene {SceneId}", profile.Name, profile.PlaySceneId);
                await _hueService.ActivateSceneAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    profile.PlaySceneId);
            }
            else if (profile.TurnOffLightsOnPlay)
            {
                _logger.LogInformation("[{ProfileName}] Turning off lights in group {GroupId}", profile.Name, profile.TargetGroupId);
                await _hueService.SetGroupStateAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = false, TransitionTime = profile.TransitionDuration });
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Dimming lights to {Brightness}", profile.Name, profile.PlayBrightness);
                await _hueService.SetGroupStateAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PlayBrightness, TransitionTime = profile.TransitionDuration });
            }
        }

        private async Task HandlePausedStateAsync(PluginConfiguration config, LightControlProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.PauseSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating pause scene {SceneId}", profile.Name, profile.PauseSceneId);
                await _hueService.ActivateSceneAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    profile.PauseSceneId);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Brightening lights to {Brightness}", profile.Name, profile.PauseBrightness);
                await _hueService.SetGroupStateAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PauseBrightness, TransitionTime = profile.TransitionDuration });
            }
        }

        private async Task HandleStoppedStateAsync(PluginConfiguration config, LightControlProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.StopSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating stop scene {SceneId}", profile.Name, profile.StopSceneId);
                await _hueService.ActivateSceneAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    profile.StopSceneId);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Turning lights on to {Brightness}", profile.Name, profile.StopBrightness);
                await _hueService.SetGroupStateAsync(
                    config.BridgeIpAddress,
                    config.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.StopBrightness, TransitionTime = profile.TransitionDuration });
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
