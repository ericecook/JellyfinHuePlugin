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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Managers
{
    /// <summary>
    /// Wires Jellyfin session events to light commands. Decisions come from
    /// <see cref="SessionPolicy"/>; sends go through one <see cref="SessionCommandQueue"/>
    /// per session into <see cref="LightCommandExecutor"/>.
    /// </summary>
    public class PlaybackSessionManager : IHostedService, IDisposable
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackSessionManager> _logger;
        private readonly LightCommandExecutor _executor;
        private readonly IHueConfiguration _configuration;
        private readonly IMediaSegmentManager _segmentManager;
        private readonly ILibraryManager _libraryManager;
        private readonly TimeProvider _clock;

        /// <summary>One entry per Jellyfin session id, kept until its stop finishes with nothing to resume, or the plugin is disposed.</summary>
        private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();

        /// <summary>Handlers started by raised events, so tests can wait for them without sleeping.</summary>
        private readonly ConcurrentDictionary<Task, byte> _inFlightHandlers = new();

        /// <summary>0 = running, 1 = stopped. An int so Stop() can gate with a single atomic exchange.</summary>
        private int _stopped;

        /// <summary>0 = never started, 1 = StartAsync has subscribed. Gates a second subscription.</summary>
        private int _started;

        /// <summary>Whether Stop() has run (or is running). Test/guard reads only; Stop() uses Interlocked.Exchange directly.</summary>
        private bool Stopped => Volatile.Read(ref _stopped) == 1;

        private sealed class SessionEntry
        {
            public readonly object Gate = new();
            /// <summary>Read and written only under Gate. Null between a stop and the next start.</summary>
            public SessionSnapshot? State;
            public readonly SessionCommandQueue Queue = new();
        }

        public PlaybackSessionManager(
            ISessionManager sessionManager,
            ILogger<PlaybackSessionManager> logger,
            LightCommandExecutor executor,
            IHueConfiguration configuration,
            IMediaSegmentManager segmentManager,
            ILibraryManager libraryManager,
            TimeProvider timeProvider)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _executor = executor;
            _configuration = configuration;
            _segmentManager = segmentManager;
            _libraryManager = libraryManager;
            _clock = timeProvider;
        }

        /// <summary>Movie/episode detection by entity type. Anything else, including null, is neither.</summary>
        internal static (bool IsMovie, bool IsEpisode) ClassifyItem(BaseItem? item)
            => (item is Movie, item is Episode);

        /// <summary>Jellyfin's item id, or null when there is no item or it has the empty id.</summary>
        internal static Guid? ItemIdOf(BaseItem? item) => item == null || item.Id == Guid.Empty ? null : item.Id;

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
            => await TrackAsync(OnPlaybackStartAsync(e), "playback start");

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
            => await TrackAsync(OnPlaybackStoppedAsync(e), "playback stop");

        private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
            => await TrackAsync(OnPlaybackProgressAsync(e), "playback progress");

        private async void OnSessionEnded(object? sender, SessionEventArgs e)
            => await TrackAsync(OnSessionEndedAsync(e), "session ended");

        private async Task TrackAsync(Task handler, string what)
        {
            _inFlightHandlers[handler] = 0;
            try
            {
                await handler;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling {What}", what);
            }
            finally
            {
                _inFlightHandlers.TryRemove(handler, out _);
            }
        }

        /// <summary>Number of tracked session entries. Test hook only.</summary>
        internal int SessionCount => _sessions.Count;

        /// <summary>Completes when every handler started by a raised event has finished. Tests only.</summary>
        internal async Task WhenIdleAsync()
        {
            while (!_inFlightHandlers.IsEmpty)
            {
                await Task.Yield();
                try
                {
                    await Task.WhenAll(_inFlightHandlers.Keys);
                }
                catch (Exception)
                {
                    // Handler failures are logged by TrackAsync; idle only cares that they finished.
                }
            }
        }

        internal async Task OnPlaybackStartAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            var config = _configuration.Current;

            var (isMovie, isEpisode) = ClassifyItem(e.Item);

            var profile = TryMatchProfile(e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, isMovie, isEpisode, config);

            if (profile == null)
            {
                // A profile stored for an earlier item on this session must not drive lights for this one.
                // The entry and its queue stay so a pending stop for the earlier item can still finish.
                if (_sessions.TryGetValue(e.Session.Id, out var stale))
                {
                    lock (stale.Gate)
                    {
                        stale.State = null;
                    }
                }

                return;
            }

            var mediaType = e.Item?.GetType().Name ?? "Unknown";
            _logger.LogInformation("Playback started on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}, Type: {MediaType}) - Using profile: {ProfileName}",
                e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, mediaType, profile.Name);

            var outroSegments = profile.EnableOutroLights
                ? await LoadOutroSegmentsAsync(e.Item)
                : Array.Empty<TickRange>();

            if (Stopped) return;

            var entry = _sessions.GetOrAdd(e.Session.Id, _ => new SessionEntry());
            lock (entry.Gate)
            {
                entry.State = new SessionSnapshot(profile, _clock.GetUtcNow(), outroSegments, PlaybackState.Playing, false)
                {
                    ItemId = ItemIdOf(e.Item)
                };
            }

            var bridge = ResolveBridge(config, profile);
            if (bridge == null) return;

            await EnqueueAsync(entry.Queue, e.Session.Id, LightAction.Play, bridge, profile);
        }

        internal async Task OnPlaybackProgressAsync(PlaybackProgressEventArgs e)
        {
            if (e.Session == null) return;

            if (!_sessions.TryGetValue(e.Session.Id, out var entry))
            {
                return;
            }

            var config = _configuration.Current;
            var input = new ProgressInput(e.IsPaused, e.PlaybackPositionTicks ?? 0, config.EnablePlugin, _clock.GetUtcNow());
            var progressItem = ItemIdOf(e.Item);

            SessionSnapshot? before = null;
            ProgressResult? result = null;
            Guid? ignoredForCurrentItem = null;
            lock (entry.Gate)
            {
                if (entry.State == null)
                {
                    return;
                }

                // Jellyfin raises each event on its own task, so a trailing progress report for the
                // previous item can arrive after the next item's start. That report must not touch the new item.
                if (entry.State is { ItemId: Guid current } && progressItem is Guid progress && current != progress)
                {
                    ignoredForCurrentItem = current;
                }
                else
                {
                    before = entry.State;
                    result = SessionPolicy.OnProgress(before, input);
                    entry.State = result.State;
                }
            }

            if (ignoredForCurrentItem is Guid ignoredFor)
            {
                _logger.LogDebug("Ignoring progress for item {ProgressItem}: session {SessionId} is playing {CurrentItem}", progressItem, e.Session.Id, ignoredFor);
                return;
            }

            var progressResult = result!;
            var profile = progressResult.State.Profile;
            switch (progressResult.Decision)
            {
                case ProgressDecision.Outro:
                    _logger.LogInformation("[{ProfileName}] Outro segment detected at {Position:F1}s - triggering stop lights on {ClientName}",
                        profile.Name, input.PositionTicks / (double)TimeSpan.TicksPerSecond, e.ClientName);
                    break;
                case ProgressDecision.GraceIgnored:
                    _logger.LogInformation("[{ProfileName}] Pause ignored — within grace period ({ElapsedSeconds}s < {GracePeriod}s) on {ClientName}",
                        profile.Name, (int)(input.Now - before!.StartedAt).TotalSeconds, profile.PauseGracePeriodSeconds, e.ClientName);
                    break;
                case ProgressDecision.Pause:
                case ProgressDecision.Resume:
                    _logger.LogInformation("Playback state changed to {State} on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - Using profile: {ProfileName}",
                        progressResult.State.PlaybackState, e.ClientName, e.DeviceId, e.Session.RemoteEndPoint, profile.Name);
                    break;
            }

            if (progressResult.Action is not LightAction action)
            {
                return;
            }

            var bridge = ResolveBridge(config, profile);
            if (bridge == null) return;

            await EnqueueAsync(entry.Queue, e.Session.Id, action, bridge, profile);
        }

        internal async Task OnPlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            if (e.Session == null) return;

            var config = _configuration.Current;

            // The entry stays in the map (created here if it doesn't exist yet) so a start
            // that follows always shares its queue with this stop.
            var entry = _sessions.GetOrAdd(e.Session.Id, _ => new SessionEntry());
            var stoppedItem = ItemIdOf(e.Item);
            SessionSnapshot? snapshot = null;
            Guid? ignoredForCurrentItem = null;
            lock (entry.Gate)
            {
                // Jellyfin raises each event on its own task, so an auto-play stop for the previous
                // item can arrive after the next item's start. That stop must not touch the new item.
                if (entry.State is { ItemId: Guid current } && stoppedItem is Guid stopped && current != stopped)
                {
                    ignoredForCurrentItem = current;
                }
                else
                {
                    snapshot = entry.State;
                    entry.State = null;
                }
            }

            if (ignoredForCurrentItem is Guid ignoredFor)
            {
                _logger.LogDebug("Ignoring stop for item {StoppedItem}: session {SessionId} is playing {CurrentItem}", stoppedItem, e.Session.Id, ignoredFor);
                return;
            }

            var profile = snapshot?.Profile;
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
            if (bridge == null) return;

            await EnqueueAsync(entry.Queue, e.Session.Id, LightAction.Stop, bridge, profile);
        }

        internal async Task OnSessionEndedAsync(SessionEventArgs e)
        {
            var session = e.SessionInfo;
            if (session == null) return;

            if (!_sessions.TryGetValue(session.Id, out var entry))
            {
                return;
            }

            SessionSnapshot? snapshot;
            lock (entry.Gate)
            {
                snapshot = entry.State;
                entry.State = null;
            }

            if (snapshot == null)
            {
                // Already stopped; the stop restored the lights. Remove the entry unless a
                // start reclaimed it since the lock above was released.
                lock (entry.Gate)
                {
                    if (entry.State == null)
                    {
                        _sessions.TryRemove(new KeyValuePair<string, SessionEntry>(session.Id, entry));
                    }
                }

                return;
            }

            var config = _configuration.Current;
            var profile = snapshot.Profile;

            _logger.LogInformation("Session ended on {ClientName} (Device: {DeviceId}, IP: {RemoteEndpoint}) - restoring lights using profile: {ProfileName}",
                session.Client, session.DeviceId, session.RemoteEndPoint, profile.Name);

            var bridge = ResolveBridge(config, profile);
            if (bridge != null)
            {
                await EnqueueAsync(entry.Queue, session.Id, LightAction.Stop, bridge, profile);
            }

            // Remove the entry now that the stop has run (or there was no bridge to send to),
            // unless a start reclaimed it while we were sending.
            lock (entry.Gate)
            {
                if (entry.State == null)
                {
                    _sessions.TryRemove(new KeyValuePair<string, SessionEntry>(session.Id, entry));
                }
            }
        }

        private Task EnqueueAsync(SessionCommandQueue queue, string sessionId, LightAction action, HueBridge bridge, LightControlProfile profile)
        {
            return queue.Enqueue(async ct =>
            {
                if (Stopped) return;

                try
                {
                    await _executor.ExecuteAsync(action, bridge, profile, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Light command {Action} superseded for session {SessionId}", action, sessionId);
                }
            });
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

        /// <summary>Resolves the profile's bridge, warning once when there is none. Every send path uses this.</summary>
        private HueBridge? ResolveBridge(PluginConfiguration config, LightControlProfile profile)
        {
            var bridge = config.FindProfileBridge(profile);

            if (bridge == null)
            {
                _logger.LogWarning("No bridge found for profile {ProfileName} (BridgeId: {BridgeId})", profile.Name, profile.BridgeId);
            }

            return bridge;
        }

        /// <summary>
        /// Subscribes to Jellyfin's session events. Called once by the host after every plugin is
        /// loaded. A second call subscribes nothing (every event would otherwise be handled
        /// twice), and a call after <see cref="StopAsync"/> or <see cref="Dispose"/> subscribes
        /// nothing at all - the instance is finished.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Stopped || Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.SessionEnded += OnSessionEnded;
            _logger.LogInformation("Playback listener started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.SessionEnded -= OnSessionEnded;

            // Cancel everything in flight and forget the sessions. Nothing is sent to any bridge;
            // the next stop restores the lights through a fresh profile match.
            foreach (var entry in _sessions.Values)
            {
                entry.Queue.Cancel();
            }

            _sessions.Clear();
            _logger.LogInformation("Playback listener stopped");
        }
    }
}
