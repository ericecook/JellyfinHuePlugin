using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    public class PlaybackSessionManagerConcurrencyTests : IDisposable
    {
        private readonly Mock<ISessionManager> _mockSessionManager;
        private readonly Mock<HueService> _mockHueService;
        private readonly Mock<IMediaSegmentManager> _mockSegmentManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly PlaybackSessionManager _manager;
        private readonly PluginConfiguration _config;

        public PlaybackSessionManagerConcurrencyTests()
        {
            _mockSessionManager = new Mock<ISessionManager>();
            _mockHueService = new Mock<HueService>(
                new NullLogger<HueService>()) { CallBase = false };
            _mockSegmentManager = new Mock<IMediaSegmentManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();

            _config = new PluginConfiguration
            {
                EnablePlugin = true,
                BridgeIpAddress = "192.168.1.50",
                Username = "testuser",
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile
                    {
                        Name = "Catch All",
                        EnableForMovies = true,
                        EnableForTvShows = true,
                        PlayBrightness = 20,
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

        [Fact]
        public async Task ConcurrentStartAndStop_DoesNotThrow()
        {
            // Arrange — simulate 20 concurrent sessions starting and stopping
            var tasks = new List<Task>();
            var exceptions = new List<Exception>();

            for (int i = 0; i < 20; i++)
            {
                var sessionId = $"session{i}";
                var session = new SessionInfo(_mockSessionManager.Object, new NullLogger<SessionInfo>())
                {
                    Id = sessionId,
                    RemoteEndPoint = $"192.168.1.{100 + i}"
                };

                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        // Start playback
                        _mockSessionManager.Raise(s => s.PlaybackStart += null, null,
                            new PlaybackProgressEventArgs
                            {
                                ClientName = "TestClient",
                                DeviceId = $"device{i}",
                                Session = session,
                                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
                            });
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) { exceptions.Add(ex); }
                    }
                }));

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Small delay then stop
                        await Task.Delay(10);
                        _mockSessionManager.Raise(s => s.PlaybackStopped += null, null,
                            new PlaybackStopEventArgs
                            {
                                ClientName = "TestClient",
                                DeviceId = $"device{i}",
                                Session = session,
                                Item = new MediaBrowser.Controller.Entities.Movies.Movie()
                            });
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) { exceptions.Add(ex); }
                    }
                }));
            }

            // Act
            await Task.WhenAll(tasks);

            // Wait for async handlers to finish
            await Task.Delay(500);

            // Assert — no exceptions from concurrent access
            exceptions.Should().BeEmpty("concurrent start/stop should not cause exceptions");
        }

        [Fact]
        public async Task ConcurrentProgressUpdates_DoesNotThrow()
        {
            // Arrange — start a session first
            var session = new SessionInfo(_mockSessionManager.Object, new NullLogger<SessionInfo>())
            {
                Id = "session1",
                RemoteEndPoint = "192.168.1.100"
            };

            _mockSessionManager.Raise(s => s.PlaybackStart += null, null,
                new PlaybackProgressEventArgs
                {
                    ClientName = "TestClient",
                    DeviceId = "device1",
                    Session = session,
                    Item = new MediaBrowser.Controller.Entities.Movies.Movie()
                });
            await Task.Delay(200);

            // Act — fire many concurrent progress updates
            var tasks = new List<Task>();
            var exceptions = new List<Exception>();

            for (int i = 0; i < 50; i++)
            {
                var isPaused = i % 2 == 0;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        _mockSessionManager.Raise(s => s.PlaybackProgress += null, null,
                            new PlaybackProgressEventArgs
                            {
                                ClientName = "TestClient",
                                DeviceId = "device1",
                                Session = session,
                                Item = new MediaBrowser.Controller.Entities.Movies.Movie(),
                                IsPaused = isPaused
                            });
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) { exceptions.Add(ex); }
                    }
                }));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(500);

            // Assert
            exceptions.Should().BeEmpty("concurrent progress updates should not cause exceptions");
        }
    }
}
