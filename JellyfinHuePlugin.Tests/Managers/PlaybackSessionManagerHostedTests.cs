using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using JellyfinHuePlugin.Tests.Support;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    /// <summary>
    /// IHostedService lifecycle: events before StartAsync are ignored, StopAsync/Dispose
    /// unsubscribe and are idempotent. Event handling itself is covered by
    /// PlaybackSessionManagerHandlerTests and PlaybackSessionManagerStateTests, which start
    /// the manager as part of their fixture.
    /// </summary>
    public class PlaybackSessionManagerHostedTests : IDisposable
    {
        private sealed class CapturingLogger : ILogger<PlaybackSessionManager>
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }

        private readonly Mock<ISessionManager> _sessionManager;
        private readonly Mock<HueService> _hue;
        private readonly Mock<HueResourceCatalog> _catalog;
        private readonly Mock<IMediaSegmentManager> _segmentManager;
        private readonly Mock<ILibraryManager> _libraryManager;
        private readonly CapturingLogger _log = new();
        private readonly PluginConfiguration _config;
        private readonly PlaybackSessionManager _manager;

        public PlaybackSessionManagerHostedTests()
        {
            _sessionManager = new Mock<ISessionManager>();
            _hue = new Mock<HueService>(
                new NullLogger<HueService>(), new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance)) { CallBase = false };
            _segmentManager = new Mock<IMediaSegmentManager>();
            _libraryManager = new Mock<ILibraryManager>();

            var testBridge = new HueBridge
            {
                Id = "bridge1",
                Name = "Test Bridge",
                IpAddress = "192.168.1.50",
                Username = "testuser"
            };

            _config = new PluginConfiguration
            {
                EnablePlugin = true,
                Bridges = new List<HueBridge> { testBridge },
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile
                    {
                        Name = "Test Profile",
                        BridgeId = "bridge1",
                        EnableForMovies = true,
                        EnableForTvShows = true,
                        PlayBrightness = 20,
                        PauseBrightness = 60,
                        StopBrightness = 100,
                        TargetGroupId = "1"
                    }
                }
            };

            _hue.Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _hue.Setup(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _catalog = new Mock<HueResourceCatalog>(_hue.Object, NullLogger<HueResourceCatalog>.Instance) { CallBase = false };
            _catalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string target, CancellationToken _) => "gl-" + target);
            _catalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string scene, CancellationToken _) => scene);

            _manager = new PlaybackSessionManager(
                _sessionManager.Object,
                _log,
                new LightCommandExecutor(_hue.Object, _catalog.Object, NullLogger<LightCommandExecutor>.Instance),
                new FakeHueConfiguration(_config),
                _segmentManager.Object,
                _libraryManager.Object,
                TimeProvider.System);
        }

        public void Dispose() => _manager.Dispose();

        private SessionInfo CreateSession(string id = "session1", string remoteEndPoint = "192.168.1.100")
        {
            return new SessionInfo(_sessionManager.Object, new NullLogger<SessionInfo>())
            {
                Id = id,
                RemoteEndPoint = remoteEndPoint
            };
        }

        /// <summary>A PlaybackProgressEventArgs for a movie on a session that matches the configured profile.</summary>
        private PlaybackProgressEventArgs StartArgs() => new()
        {
            ClientName = "TestClient",
            DeviceId = "device1",
            Session = CreateSession(),
            Item = new MediaBrowser.Controller.Entities.Movies.Movie()
        };

        [Fact]
        public async Task EventBeforeStart_SendsNothing()
        {
            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task EventAfterStart_Sends()
        {
            await _manager.StartAsync(CancellationToken.None);

            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task EventAfterStop_SendsNothing_AndSessionsAreGone()
        {
            await _manager.StartAsync(CancellationToken.None);
            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            await _manager.StopAsync(CancellationToken.None);
            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            _manager.SessionCount.Should().Be(0);
            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task StartTwice_SubscribesOnce()
        {
            await _manager.StartAsync(CancellationToken.None);
            await _manager.StartAsync(CancellationToken.None);

            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Once);
            _log.Lines.Count(l => l == "Playback listener started").Should().Be(1);
        }

        [Fact]
        public async Task StartAfterStop_SubscribesNothing()
        {
            await _manager.StartAsync(CancellationToken.None);
            await _manager.StopAsync(CancellationToken.None);

            await _manager.StartAsync(CancellationToken.None);
            _sessionManager.Raise(s => s.PlaybackStart += null, StartArgs());
            await _manager.WhenIdleAsync();

            _hue.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task StopAndDisposeTwice_DoNotThrow_AndLogOnce()
        {
            await _manager.StartAsync(CancellationToken.None);

            await _manager.StopAsync(CancellationToken.None);
            _manager.Dispose();
            _manager.Dispose();

            _log.Lines.Count(l => l == "Playback listener stopped").Should().Be(1);
            _log.Lines.Should().Contain("Playback listener started");
        }
    }
}
