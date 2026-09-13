using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using MediaBrowser.Controller.Configuration;
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
    /// Event wiring only: raises the ISessionManager events through Moq and waits for the
    /// async void handlers with WhenIdleAsync. Behaviour lives in PlaybackSessionManagerStateTests.
    /// </summary>
    public class PlaybackSessionManagerHandlerTests : IDisposable
    {
        private readonly Mock<ISessionManager> _mockSessionManager;
        private readonly Mock<HueService> _mockHueService;
        private readonly Mock<HueResourceCatalog> _mockCatalog;
        private readonly Mock<IMediaSegmentManager> _mockSegmentManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly PlaybackSessionManager _manager;
        private PluginConfiguration _config;

        public PlaybackSessionManagerHandlerTests()
        {
            _mockSessionManager = new Mock<ISessionManager>();
            _mockHueService = new Mock<HueService>(
                new NullLogger<HueService>()) { CallBase = false };
            _mockSegmentManager = new Mock<IMediaSegmentManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();

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

            _mockHueService
                .Setup(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockHueService
                .Setup(h => h.RecallSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _mockCatalog = new Mock<HueResourceCatalog>(_mockHueService.Object, NullLogger.Instance) { CallBase = false };
            _mockCatalog.Setup(c => c.ResolveGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string target, CancellationToken _) => "gl-" + target);
            _mockCatalog.Setup(c => c.ResolveSceneAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HueBridge _, string scene, CancellationToken _) => scene);

            _manager = new PlaybackSessionManager(
                _mockSessionManager.Object,
                new NullLogger<PlaybackSessionManager>(),
                _mockHueService.Object,
                _mockCatalog.Object,
                () => _config,
                _mockSegmentManager.Object,
                _mockLibraryManager.Object);
        }

        public void Dispose()
        {
            _manager.Dispose();
        }

        private SessionInfo CreateSession(string id = "session1", string remoteEndPoint = "192.168.1.100")
        {
            return new SessionInfo(_mockSessionManager.Object, new NullLogger<SessionInfo>())
            {
                Id = id,
                RemoteEndPoint = remoteEndPoint
            };
        }

        private void VerifyGroupedLight(Func<GroupedLightState, bool> match, Times times) =>
            _mockHueService.Verify(h => h.SetGroupedLightAsync(It.Is<HueBridge>(b => b.Id == "bridge1"), "gl-1",
                It.Is<GroupedLightState>(s => match(s)), It.IsAny<CancellationToken>()), times);

        [Fact]
        public async Task PlaybackStart_WithBrightness_SetsGroupState()
        {
            // Arrange
            var session = CreateSession();
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act — raise the event
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);

            // Wait for the async void handler and its light command
            await _manager.WhenIdleAsync();

            // Assert
            VerifyGroupedLight(s => s.On == true && s.Brightness == 20, Times.Once());
        }

        [Fact]
        public async Task PlaybackStart_WithScene_ActivatesScene()
        {
            // Arrange
            _config.Profiles[0].PlaySceneId = "scene123";

            var session = CreateSession();
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);
            await _manager.WhenIdleAsync();

            // Assert
            _mockHueService.Verify(h => h.RecallSceneAsync(It.Is<HueBridge>(b => b.Id == "bridge1"), "scene123", null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PlaybackStart_TurnOffLights_SetsOnFalse()
        {
            // Arrange
            _config.Profiles[0].TurnOffLightsOnPlay = true;

            var session = CreateSession();
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);
            await _manager.WhenIdleAsync();

            // Assert
            VerifyGroupedLight(s => s.On == false, Times.Once());
        }

        [Fact]
        public async Task PlaybackStopped_RestoresBrightness()
        {
            // Arrange — first start playback to register the session
            var session = CreateSession();
            var startArgs = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            _mockSessionManager.Raise(s => s.PlaybackStart += null, startArgs);
            await _manager.WhenIdleAsync();

            // Act — stop playback
            var stopArgs = new PlaybackStopEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            _mockSessionManager.Raise(s => s.PlaybackStopped += null, stopArgs);
            await _manager.WhenIdleAsync();

            // Assert
            VerifyGroupedLight(s => s.On == true && s.Brightness == 100, Times.Once());
        }

        [Fact]
        public async Task SessionEnded_Raised_RestoresLights()
        {
            var session = CreateSession();
            var startArgs = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };
            _mockSessionManager.Raise(s => s.PlaybackStart += null, startArgs);
            await _manager.WhenIdleAsync();

            _mockSessionManager.Raise(s => s.SessionEnded += null, new SessionEventArgs { SessionInfo = session });
            await _manager.WhenIdleAsync();

            VerifyGroupedLight(s => s.On == true && s.Brightness == 100, Times.Once());
        }

        [Fact]
        public async Task PlaybackProgress_PauseDetected_BrightensLights()
        {
            // Arrange — start playback first
            var session = CreateSession();
            var startArgs = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            _mockSessionManager.Raise(s => s.PlaybackStart += null, startArgs);
            await _manager.WhenIdleAsync();

            // Act — send progress with IsPaused=true
            var progressArgs = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie(),
                IsPaused = true
            };

            _mockSessionManager.Raise(s => s.PlaybackProgress += null, progressArgs);
            await _manager.WhenIdleAsync();

            // Assert — should set pause brightness
            VerifyGroupedLight(s => s.On == true && s.Brightness == 60, Times.Once());
        }

        [Fact]
        public async Task PlaybackStart_NullSession_DoesNotThrow()
        {
            // Arrange
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = null!,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act — should not throw
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);
            await _manager.WhenIdleAsync();

            // Assert — no calls made
            _mockHueService.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PlaybackStart_NoMatchingProfile_DoesNotControlLights()
        {
            // Arrange — profile only matches TV shows, but item is a movie with EnableForMovies=false
            _config.Profiles[0].EnableForMovies = false;
            _config.Profiles[0].EnableForTvShows = true;

            var session = CreateSession();
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);
            await _manager.WhenIdleAsync();

            // Assert
            _mockHueService.Verify(h => h.SetGroupedLightAsync(It.IsAny<HueBridge>(), It.IsAny<string>(), It.IsAny<GroupedLightState>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PlaybackStart_WithTransition_IncludesTransitionTime()
        {
            // Arrange
            _config.Profiles[0].EnablePlayTransition = true;
            _config.Profiles[0].PlayTransitionDuration = 10;

            var session = CreateSession();
            var args = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            // Act
            _mockSessionManager.Raise(s => s.PlaybackStart += null, args);
            await _manager.WhenIdleAsync();

            // Assert
            VerifyGroupedLight(s => s.DurationMs == 1000, Times.Once());
        }
    }
}
