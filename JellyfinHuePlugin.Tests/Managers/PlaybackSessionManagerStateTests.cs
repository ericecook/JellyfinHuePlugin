using System;
using System.Collections.Generic;
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
    /// Per-session behaviour: profile reuse, grace timing, outro caching. The handlers are
    /// awaited directly, so there are no sleeps. PlaybackSessionManagerHandlerTests covers
    /// the event wiring.
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
        private readonly Mock<IMediaSegmentManager> _segments = new();
        private readonly Mock<ILibraryManager> _library = new();
        private readonly FakeClock _clock = new();
        private readonly PluginConfiguration _config;
        private readonly PlaybackSessionManager _manager;

        public PlaybackSessionManagerStateTests()
        {
            _hue = new Mock<HueService>(new NullLogger<HueService>()) { CallBase = false };
            _hue.Setup(h => h.SetGroupStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _hue.Setup(h => h.ActivateSceneAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

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
            PauseBrightness = 100,
            StopBrightness = 254,
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

        private void VerifyBrightness(int bri, Times times) =>
            _hue.Verify(h => h.SetGroupStateAsync("192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.On == true && s.Bri == bri), It.IsAny<CancellationToken>()), times);

        private void VerifyTotalGroupCalls(Times times) =>
            _hue.Verify(h => h.SetGroupStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()), times);

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

            VerifyBrightness(100, Times.Once());
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
            VerifyBrightness(254, Times.Once());

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(100, Times.Never());
        }

        [Fact]
        public async Task Stop_WithoutStart_FallsBackToMatching()
        {
            await _manager.OnPlaybackStoppedAsync(Stop(Session(), new Movie()));

            VerifyBrightness(254, Times.Once());
        }

        [Fact]
        public async Task Start_NoMatch_RemovesStaleEntry()
        {
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _config.Profiles[0].EnableForMovies = false;
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));

            VerifyBrightness(100, Times.Never());
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

            VerifyBrightness(254, Times.Once());
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

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Progress_GraceUsesElapsedTimeNotPosition()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(31));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true, positionSeconds: 5));

            VerifyBrightness(100, Times.Once());
        }

        [Fact]
        public async Task Progress_PauseHeldPastGracePeriod_BrightensWhenWindowCloses()
        {
            _config.Profiles[0].PauseGracePeriodSeconds = 30;
            var session = Session();
            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));

            _clock.Advance(TimeSpan.FromSeconds(10));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(100, Times.Never());

            _clock.Advance(TimeSpan.FromSeconds(21));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), paused: true));
            VerifyBrightness(100, Times.Once());

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
            VerifyBrightness(254, Times.Once());
        }

        [Fact]
        public async Task Start_OutroDisabled_DoesNotQuerySegments()
        {
            GivenOutroSegment(100, 110);
            var session = Session();

            await _manager.OnPlaybackStartAsync(Progress(session, new Movie()));
            await _manager.OnPlaybackProgressAsync(Progress(session, new Movie(), positionSeconds: 105));

            VerifySegmentQueries(Times.Never());
            VerifyBrightness(254, Times.Never());
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
            VerifyBrightness(254, Times.Never());
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

            VerifyBrightness(254, Times.Once());
            VerifyBrightness(100, Times.Never());
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

            VerifyBrightness(254, Times.Exactly(2)); // idempotent by design
        }

    }
}
