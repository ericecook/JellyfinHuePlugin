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
    public class PlaybackSessionManagerHandlerTests : IDisposable
    {
        private readonly Mock<ISessionManager> _mockSessionManager;
        private readonly Mock<HueService> _mockHueService;
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
                        PauseBrightness = 100,
                        StopBrightness = 254,
                        TargetGroupId = "1"
                    }
                }
            };

            _mockHueService
                .Setup(h => h.SetGroupStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _mockHueService
                .Setup(h => h.ActivateSceneAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _manager = new PlaybackSessionManager(
                _mockSessionManager.Object,
                new NullLogger<PlaybackSessionManager>(),
                _mockHueService.Object,
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);

            // Allow async handler to complete
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                "192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.On == true && s.Bri == 20),
                It.IsAny<CancellationToken>()), Times.Once);
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.ActivateSceneAsync(
                "192.168.1.50", "testuser", "1", "scene123",
                It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                "192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.On == false),
                It.IsAny<CancellationToken>()), Times.Once);
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

            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, startArgs);
            await Task.Delay(200);

            // Act — stop playback
            var stopArgs = new PlaybackStopEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
            };

            _mockSessionManager.Raise(s => s.PlaybackStopped += null, null, stopArgs);
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                "192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.On == true && s.Bri == 254),
                It.IsAny<CancellationToken>()), Times.Once);
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

            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, startArgs);
            await Task.Delay(200);

            // Act — send progress with IsPaused=true
            var progressArgs = new PlaybackProgressEventArgs
            {
                ClientName = "TestClient",
                DeviceId = "device1",
                Session = session,
                Item = new MediaBrowser.Controller.Entities.Movies.Movie(),
                IsPaused = true
            };

            _mockSessionManager.Raise(s => s.PlaybackProgress += null, null, progressArgs);
            await Task.Delay(200);

            // Assert — should set pause brightness
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                "192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.On == true && s.Bri == 100),
                It.IsAny<CancellationToken>()), Times.Once);
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);
            await Task.Delay(200);

            // Assert — no calls made
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()), Times.Never);
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<HueLightState>(), It.IsAny<CancellationToken>()), Times.Never);
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
            _mockSessionManager.Raise(s => s.PlaybackStart += null, null, args);
            await Task.Delay(200);

            // Assert
            _mockHueService.Verify(h => h.SetGroupStateAsync(
                "192.168.1.50", "testuser", "1",
                It.Is<HueLightState>(s => s.TransitionTime == 10),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
