using System.Linq;
using FluentAssertions;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using JellyfinHuePlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests
{
    /// <summary>
    /// The wiring proof: the registrator's graph resolves with the services Jellyfin supplies,
    /// validated on build, before any container run.
    /// </summary>
    public class PluginServiceRegistratorTests
    {
        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Mock.Of<ISessionManager>());
            services.AddSingleton(Mock.Of<IMediaSegmentManager>());
            services.AddSingleton(Mock.Of<ILibraryManager>());
            new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
            services.AddTransient<HueController>();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        [Fact]
        public void Graph_ValidatesOnBuild_AndResolvesTheController()
        {
            using var provider = Build();

            provider.GetRequiredService<HueController>().Should().NotBeNull();
        }

        [Fact]
        public void HostedService_IsThePlaybackSessionManagerSingleton()
        {
            using var provider = Build();

            var hosted = provider.GetServices<IHostedService>().ToList();

            hosted.Should().ContainSingle().Which.Should().BeSameAs(provider.GetRequiredService<PlaybackSessionManager>());
        }

        [Fact]
        public void SharedServices_AreSingletons()
        {
            using var provider = Build();

            provider.GetRequiredService<HueService>().Should().BeSameAs(provider.GetRequiredService<HueService>());
            provider.GetRequiredService<IHueConfiguration>().Should().BeSameAs(provider.GetRequiredService<HueConfigurationStore>());
        }
    }
}
