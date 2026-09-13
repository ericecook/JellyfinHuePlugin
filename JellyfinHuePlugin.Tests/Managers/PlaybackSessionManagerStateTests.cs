using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Database.Implementations.Enums;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    /// <summary>
    /// Per-session behaviour: profile reuse, grace timing, outro caching, queue ordering,
    /// session-ended and Dispose. The handlers are awaited directly and complete when their
    /// enqueued light command has finished, so there are no sleeps.
    /// PlaybackSessionManagerHandlerTests covers the event wiring.
    /// </summary>
    public class PlaybackSessionManagerStateTests : IDisposable
    {
        private sealed class FakeClock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
            public void Advance(TimeSpan by) => Now += by;
        }

        private readonly Mock<ISessionManager> _sessionManager = new();
        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly Mock<IMediaSegmentManager> _segments = new();
        private readonly Mock<ILibraryManager> _library = new();
        private readonly FakeClock _clock = new();
        private readonly List<int?> _sentBrightness = new();
        private readonly PluginConfiguration _config;
        private readonly PlaybackSessionManager _manager;

        public PlaybackSessionManagerStateTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, s, _) => _sentBrightness.Add(s.On == false ? -1 : (int?)s.Brightness))
                .ReturnsAsync(true);
            _hue.Setup(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger.Instance) { CallBase = false };
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string target, CancellationToken _) => "gl-" + target);
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string scene, CancellationToken _) => scene);

            _config = new PluginConfiguration
            {
                EnablePlugin = true,
                Bridges = new List<HueBridge>
                {
                    new HueBridge { Id = "bridge1", Name = "Test Bridge", IpAddress = "192.168.1.50", Username = "testuser" }
                },
                Profiles = new List<LightControlProfile> { MakeProfile() }
            };

            _manager = new PlaybackSessionManager(
                _sessionManager.Object,
                new NullLogger<PlaybackSessionManager>(),
                _hue.Object,
                _catalog.Object,
                () => _config,
                _segments.Object,
                _library.Object,
                _clock);
        }

        public void Dispose() => _manager.Dispose();

        private static LightControlProfile MakeProfile() => new()
        {
            Name = "Test Profile",
            BridgeId = "bridge1",
            EnableForMovies = true,
            EnableForTvShows = true,
            PlayBrightness = 20,
            PauseBrightness = 60,
            StopBrightness = 100,
            TargetGroupId = "1"
        };

        private SessionInfo Session(string id = "session1") =>
            new(_sessionManager.Object, new NullLogger<SessionInfo>()) { Id = id, RemoteEndPoint = "192.168.1.100" };

        private static PlaybackProgressEventArgs Progress(SessionInfo session, BaseItem? item, bool paused = false, long positionSeconds = 0) => new()
        {
            ClientName = "TestClient",
            DeviceId = "device1",
            Session = session,
            Item = item!,
            IsPaused = paused,
            PlaybackPositionTicks = positionSeconds * TimeSpan.TicksPerSecond
        };

        private static PlaybackStopEventArgs Stop(SessionInfo session, BaseItem? item) => new()
        {
            ClientName = "TestClient",
            DeviceId = "device1",
            Session = session,
            Item = item!
        };

        private void VerifyBrightness(int brightness, Times times) =>
            _hue.Verify(h => h.SetGroupedLightAsync(It.Is<HueBridge>(b => b.Id == "bridge1"), "gl-1",
                It.Is<GroupedLightState>(s => s.On == true && s.Brightness == brightness), It.IsAny<CancellationToken>()), times);

        private void VerifyTotalGroupCalls(Times times) =>
            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(),
                It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), times);

        private void VerifyOff(Times times) =>
            _hue.Verify(h => h.SetGroupedLightAsync(It.Is<HueBridge>(b => b.Id == "bridge1"), "gl-1",
                It.Is<GroupedLightState>(s => s.On == false), It.IsAny<CancellationToken>()), times);

        /// <summary>Blocks the next call that sends the given brightness until the returned release completes.</summary>
        private (Task Entered, TaskCompletionSource<bool> Release) GateBrightness(int brightness)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(),
                    It.Is<GroupedLightState>(s => s.Brightness == brightness), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, s, _) =>
                {
                    _sentBrightness.Add((int?)s.Brightness);
                    entered.TrySetResult();
                })
                .Returns(() => release.Task); // ignores the token: simulates a bridge call that cannot be interrupted
            return (entered.Task, release);
        }

        private static SessionEventArgs Ended(SessionInfo session) => new() { SessionInfo = session };

        private void GivenOutroSegment(long startSeconds, long endSeconds) =>
            _segments
                .Setup(s => s.GetSegmentsAsync(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<MediaSegmentType>>(),
                    It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
                .ReturnsAsync(new[]
                {
                    new MediaSegmentDto
                    {
                        Type = MediaSegmentType.Outro,
                        StartTicks = startSeconds * TimeSpan.TicksPerSecond,
                        EndTicks = endSeconds * TimeSpan.TicksPerSecond
                    }
                });

        private void VerifySegmentQueries(Times times) =>
            _segments.Verify(s => s.GetSegmentsAsync(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<MediaSegmentType>>(),
                It.IsAny<LibraryOptions>(), It.IsAny<bool>()), times);

        [Fact]
        public void ClassifyItem_RecognisesMovieAndEpisodeOnly()
        {
            PlaybackSessionManager.ClassifyItem(null).Should().Be((false, false));
            PlaybackSessionManager.ClassifyItem(new Movie()).Should().Be((true, false));
            PlaybackSessionManager.ClassifyItem(new Episode()).Should().Be((false, true));
            PlaybackSessionManager.ClassifyItem(new Video()).Should().Be((false, false));
        }

        [Fact]
        public async Task Start_ClassifiesMovieAndEpisodeByType()
        {
            _config.Profiles[0].EnableForMovies = false;

            await _manager.OnPlaybackStartAsync(Progress(Session("s1"), new Movie()));
            VerifyTotalGroupCalls(Times.Never());

            await _manager.OnPlaybackStartAsync(Progress(Session("s2"), new Episode()));
            VerifyBrightness(20, Times.Once());
        }

        [Fact]
        public async Task Progress_UsesProfileStoredAtStart()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            var nonMatching = MakeProfile();
            nonMatching.EnableForMovies = false;
            _config.Profiles = new List<LightControlProfile> { nonMatching };

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));

            VerifyBrightness(60, Times.Once());
        }

        [Fact]
        public async Task Stop_UsesProfileStoredAtStart_AndRemovesEntry()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            var nonMatching = MakeProfile();
            nonMatching.EnableForMovies = false;
            _config.Profiles = new List<LightControlProfile> { nonMatching };

            await _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));
            VerifyBrightness(100, Times.Once());

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(60, Times.Never());
        }

        [Fact]
        public async Task Stop_WithoutStart_FallsBackToMatching()
        {
            await _manager.OnPlaybackStoppedAsync(Stop(Session(), new Movie()));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Start_NoMatch_RemovesStaleEntry()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.Profiles[0].EnableForMovies = false;
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));

            VerifyBrightness(60, Times.Never());
        }

        [Fact]
        public async Task Progress_PluginDisabledMidPlayback_SendsNothing()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.EnablePlugin = false;
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));

            VerifyTotalGroupCalls(Times.Once()); // only the play call from start
        }

        [Fact]
        public async Task Stop_PluginDisabledMidPlayback_StillRestoresLights()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.EnablePlugin = false;
            await _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Progress_PauseWithinGracePeriod_IsIgnoredAndResumeIsNoOp()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(10));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: false));

            VerifyTotalGroupCalls(Times.Once());
        }

        [Fact]
        public async Task Progress_PauseAfterGracePeriod_Brightens()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(31));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));

            VerifyBrightness(60, Times.Once());
        }

        [Fact]
        public async Task Progress_GraceUsesElapsedTimeNotPosition()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(31));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true, positionSeconds: 5));

            VerifyBrightness(60, Times.Once());
        }

        [Fact]
        public async Task Progress_PauseHeldPastGracePeriod_BrightensWhenWindowCloses()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(10));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(60, Times.Never());

            _clock.Advance(TimeSpan.FromSeconds(21));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(60, Times.Once());

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: false));
            VerifyBrightness(20, Times.Exactly(2)); // play at start, play again on resume
        }

        [Fact]
        public async Task Start_LoadsOutroSegmentsOnce()
        {
            _config.Profiles[0].EnableOutroLights = true;
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 50));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 108));

            VerifySegmentQueries(Times.Once());
            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Start_OutroDisabled_DoesNotQuerySegments()
        {
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));

            VerifySegmentQueries(Times.Never());
            VerifyBrightness(100, Times.Never());
        }

        [Fact]
        public async Task Start_SegmentQueryThrows_StillControlsLights()
        {
            _config.Profiles[0].EnableOutroLights = true;
            _segments
                .Setup(s => s.GetSegmentsAsync(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<MediaSegmentType>>(),
                    It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
                .ThrowsAsync(new InvalidOperationException("database unavailable"));
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));

            VerifyBrightness(20, Times.Once());
            VerifyBrightness(100, Times.Never());
        }

        [Fact]
        public async Task Progress_AfterOutro_ResumeDoesNotDim()
        {
            _config.Profiles[0].EnableOutroLights = true;
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true, positionSeconds: 106));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: false, positionSeconds: 107));

            VerifyBrightness(100, Times.Once());
            VerifyBrightness(60, Times.Never());
            VerifyTotalGroupCalls(Times.Exactly(2)); // play, then the outro's stop
        }

        [Fact]
        public async Task Stop_AfterOutro_StillSendsStopState()
        {
            _config.Profiles[0].EnableOutroLights = true;
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));
            await _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));

            VerifyBrightness(100, Times.Exactly(2)); // idempotent by design
        }

        [Fact]
        public async Task Stop_DuringPlay_RunsAfterThePlayExits()
        {
            var (playEntered, release) = GateBrightness(20);
            var session = Session();

            var start = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await playEntered;
            var stop = _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));

            VerifyBrightness(100, Times.Never()); // queued behind the blocked play
            release.SetResult(true);
            await Task.WhenAll(start, stop);

            _sentBrightness.Should().Equal(20, 100);
        }

        [Fact]
        public async Task Progress_ConcurrentPauseReports_SendOnePauseCommand()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            await Task.WhenAll(
                _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true)),
                _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true)));

            VerifyBrightness(60, Times.Once());
        }

        [Fact]
        public async Task Progress_PauseThenResume_SendsInOrder()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            var pause = _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: false));
            await pause;

            // Latest wins: the pause either ran before the resume or was superseded, never after it.
            _sentBrightness.Last().Should().Be(20);
            _sentBrightness.Count(b => b == 60).Should().BeLessThanOrEqualTo(1);
        }

        [Fact]
        public async Task SessionEnded_WithEntry_RestoresLightsAndRemovesEntry()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            await _manager.OnSessionEndedAsync(Ended(session));
            VerifyBrightness(100, Times.Once());

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(60, Times.Never());
        }

        [Fact]
        public async Task SessionEnded_WithoutEntry_SendsNothing()
        {
            await _manager.OnSessionEndedAsync(Ended(Session()));

            VerifyTotalGroupCalls(Times.Never());
        }

        [Fact]
        public async Task SessionEnded_PluginDisabled_StillRestoresLights()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.EnablePlugin = false;
            await _manager.OnSessionEndedAsync(Ended(session));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task SessionEnded_AfterStop_SendsNothing()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));

            await _manager.OnSessionEndedAsync(Ended(session));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task SessionEnded_MissingBridge_StillRemovesEntry()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.Profiles[0].BridgeId = "no-such-bridge";
            await _manager.OnSessionEndedAsync(Ended(session));

            _manager.SessionCount.Should().Be(0);
            VerifyBrightness(100, Times.Never());
        }

        [Fact]
        public async Task StopThenStart_SameSession_ShareOneQueue()
        {
            var (stopEntered, release) = GateBrightness(100);
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            var stop = _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));
            await stopEntered;
            var start = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            VerifyBrightness(20, Times.Once()); // only the first play; the second waits behind the blocked stop
            release.SetResult(true);
            await Task.WhenAll(stop, start);

            VerifyBrightness(20, Times.Exactly(2));
        }

        [Fact]
        public async Task StopWithoutEntry_ThenStart_ShareOneQueue()
        {
            var (stopEntered, release) = GateBrightness(100);
            var session = Session();

            var stop = _manager.OnPlaybackStoppedAsync(Stop(session, new Movie()));
            await stopEntered;
            var start = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            VerifyBrightness(20, Times.Never()); // start waits behind the blocked stop, sharing its queue
            release.SetResult(true);
            await Task.WhenAll(stop, start);

            VerifyBrightness(20, Times.Once());
        }

        [Fact]
        public async Task SessionEnded_ThenStart_SameId_ShareOneQueue()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            var (stopEntered, release) = GateBrightness(100);

            var ended = _manager.OnSessionEndedAsync(Ended(session));
            await stopEntered;
            var start2 = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            VerifyBrightness(20, Times.Once()); // only the first start; start2 waits behind the blocked session-ended stop
            release.SetResult(true);
            await Task.WhenAll(ended, start2);

            VerifyBrightness(20, Times.Exactly(2));

            // The entry survived the session-ended removal because start2 reclaimed it.
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(60, Times.Once());
        }

        [Fact]
        public async Task Dispose_CancelsTheInFlightCommandAndSendsNothingAfter()
        {
            CancellationToken observed = default;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .Callback<HueBridge, string, GroupedLightState, CancellationToken>((_, _, _, token) => { observed = token; entered.TrySetResult(); })
                .Returns(() => release.Task);
            var session = Session();

            var start = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await entered.Task;
            _manager.Dispose();
            observed.IsCancellationRequested.Should().BeTrue();
            release.SetResult(true);
            await start;

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyTotalGroupCalls(Times.Once()); // only the play that was already in flight
        }

        [Fact]
        public async Task Dispose_DuringOutroSegmentLoad_SendsNothing()
        {
            _config.Profiles[0].EnableOutroLights = true;
            var tcs = new TaskCompletionSource<IEnumerable<MediaSegmentDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _segments
                .Setup(s => s.GetSegmentsAsync(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<MediaSegmentType>>(),
                    It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
                .Returns(tcs.Task);
            var session = Session();

            var start = _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            _manager.Dispose();
            tcs.SetResult(Array.Empty<MediaSegmentDto>());
            await start;

            VerifyTotalGroupCalls(Times.Never());
        }

        [Fact]
        public async Task Progress_OutroWithMissingBridge_SendsNothing()
        {
            _config.Profiles[0].EnableOutroLights = true;
            _config.Profiles[0].BridgeId = "no-such-bridge";
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true, positionSeconds: 106));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: false, positionSeconds: 107));

            VerifyTotalGroupCalls(Times.Never());
        }

        [Fact]
        public async Task Stop_ForDifferentItem_IsIgnored()
        {
            var session = Session();
            var episode1 = new Episode { Id = Guid.NewGuid() };
            var episode2 = new Episode { Id = Guid.NewGuid() };
            await _manager.OnPlaybackStartAsync(Progress(session, episode2));

            await _manager.OnPlaybackStoppedAsync(Stop(session, episode1)); // late stop for the previous item

            VerifyBrightness(100, Times.Never());
            await _manager.OnPlaybackProgressAsync(Progress(session, episode2, paused: true));
            VerifyBrightness(60, Times.Once()); // the session is still live
        }

        [Fact]
        public async Task Stop_ForSameItem_Restores()
        {
            var session = Session();
            var movie = new Movie { Id = Guid.NewGuid() };
            await _manager.OnPlaybackStartAsync(Progress(session, movie));

            await _manager.OnPlaybackStoppedAsync(Stop(session, movie));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Stop_WithoutItem_StillRestores()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie { Id = Guid.NewGuid() }));

            await _manager.OnPlaybackStoppedAsync(Stop(session, null));

            VerifyBrightness(100, Times.Once());
        }
    }
}
