using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Managers;
using Xunit;

namespace JellyfinHuePlugin.Tests.Managers
{
    /// <summary>
    /// Exercises the real matcher. Requests default to a movie because the matcher
    /// requires a recognised media type and profiles enable movies by default.
    /// </summary>
    public class ProfileMatcherTests
    {
        private static MatchRequest Req(string clientName, string deviceId, string remoteEndpoint, bool isMovie = true, bool isEpisode = false)
            => new(clientName, deviceId, remoteEndpoint, isMovie, isEpisode);

        private static LightControlProfile? Find(PluginConfiguration config, string clientName, string deviceId, string remoteEndpoint)
            => ProfileMatcher.FindMatchingProfile(config, Req(clientName, deviceId, remoteEndpoint)).Profile;

        private static PluginConfiguration ConfigWith(params LightControlProfile[] profiles)
            => new() { EnablePlugin = true, Profiles = profiles.ToList() };

        #region Basic Profile Matching Tests

        [Fact]
        public void ProfileMatching_NoProfilesConfigured_ShouldReturnNull()
        {
            var config = ConfigWith();

            Find(config, "Roku", "device1", "192.168.1.100").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_PluginDisabled_ShouldReturnNull()
        {
            var config = new PluginConfiguration
            {
                EnablePlugin = false,
                Profiles = new List<LightControlProfile>
                {
                    new LightControlProfile { Name = "Test Profile", TargetClientName = "Roku" }
                }
            };

            Find(config, "Roku", "device1", "192.168.1.100").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_NoFiltersInProfile_ShouldMatchAll()
        {
            var config = ConfigWith(new LightControlProfile
            {
                Name = "Match All",
                TargetClientName = string.Empty,
                TargetDeviceIds = new List<string>(),
                TargetIpAddress = string.Empty
            });

            Find(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            Find(config, "Web", "device2", "192.168.1.101").Should().NotBeNull();
            Find(config, "Android", "device3", "192.168.1.102").Should().NotBeNull();
        }

        #endregion

        #region Client Name Filtering Tests

        [Theory]
        [InlineData("Roku", "Roku-Living-Room", true)]
        [InlineData("Roku", "FireTV-Bedroom", false)]
        [InlineData("Web", "Web-Browser", true)]
        [InlineData("Web", "Roku", false)]
        public void ProfileMatching_ClientNameOnly_ShouldMatchSubstring(string filterName, string actualName, bool shouldMatch)
        {
            var config = ConfigWith(new LightControlProfile { Name = "Client Filter", TargetClientName = filterName });

            var profile = Find(config, actualName, "deviceId", "192.168.1.100");

            (profile != null).Should().Be(shouldMatch);
        }

        [Fact]
        public void ProfileMatching_ClientName_ShouldBeCaseInsensitive()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Case Test", TargetClientName = "roku" });

            Find(config, "ROKU", "device1", "192.168.1.100").Should().NotBeNull();
            Find(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            Find(config, "rOkU", "device1", "192.168.1.100").Should().NotBeNull();
        }

        #endregion

        #region Device ID Filtering Tests

        [Fact]
        public void ProfileMatching_SingleDeviceId_ShouldMatchExactDevice()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Single Device", TargetDeviceIds = new List<string> { "roku-123" } });

            Find(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            Find(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_MultipleDeviceIds_ShouldMatchAny()
        {
            var config = ConfigWith(new LightControlProfile
            {
                Name = "Multiple Devices",
                TargetDeviceIds = new List<string> { "roku-123", "firetv-456", "appletv-789" }
            });

            Find(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            Find(config, "FireTV", "firetv-456", "192.168.1.101").Should().NotBeNull();
            Find(config, "AppleTV", "appletv-789", "192.168.1.102").Should().NotBeNull();
            Find(config, "Web", "web-browser", "192.168.1.103").Should().BeNull();
        }

        [Theory]
        [InlineData("roku-123", "roku-123", true)]
        [InlineData("roku-123", "ROKU-123", true)]
        [InlineData("roku-123", "roku-456", false)]
        public void ProfileMatching_DeviceId_ShouldBeCaseInsensitive(string configuredId, string actualId, bool shouldMatch)
        {
            var config = ConfigWith(new LightControlProfile { Name = "Case Test", TargetDeviceIds = new List<string> { configuredId } });

            var profile = Find(config, "Roku", actualId, "192.168.1.100");

            (profile != null).Should().Be(shouldMatch);
        }

        #endregion

        #region IP Address Filtering Tests

        [Fact]
        public void ProfileMatching_IpAddressOnly_ShouldMatchExactIp()
        {
            var config = ConfigWith(new LightControlProfile { Name = "IP Filter", TargetIpAddress = "192.168.1.100" });

            Find(config, "Roku", "device1", "192.168.1.100").Should().NotBeNull();
            Find(config, "Roku", "device1", "192.168.1.101").Should().BeNull();
        }

        [Fact]
        public void ProfileMatching_IpAddress_ShouldStripPort()
        {
            var config = ConfigWith(new LightControlProfile { Name = "IP Filter", TargetIpAddress = "192.168.1.100" });

            Find(config, "Roku", "device1", "192.168.1.100:8096").Should().NotBeNull();
        }

        #endregion

        #region Combined Filter Tests (AND Logic)

        [Fact]
        public void ProfileMatching_ClientAndDevice_ShouldRequireBoth()
        {
            var config = ConfigWith(new LightControlProfile
            {
                Name = "Combined",
                TargetClientName = "Roku",
                TargetDeviceIds = new List<string> { "roku-123" }
            });

            Find(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            Find(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull("wrong device");
            Find(config, "Web", "roku-123", "192.168.1.100").Should().BeNull("wrong client");
        }

        [Fact]
        public void ProfileMatching_AllThreeFilters_ShouldRequireAll()
        {
            var config = ConfigWith(new LightControlProfile
            {
                Name = "Triple Filter",
                TargetClientName = "Roku",
                TargetDeviceIds = new List<string> { "roku-123" },
                TargetIpAddress = "192.168.1.100"
            });

            Find(config, "Roku", "roku-123", "192.168.1.100").Should().NotBeNull();
            Find(config, "Roku", "roku-123", "192.168.1.101").Should().BeNull("wrong IP");
            Find(config, "Roku", "roku-456", "192.168.1.100").Should().BeNull("wrong device");
            Find(config, "Web", "roku-123", "192.168.1.100").Should().BeNull("wrong client");
        }

        #endregion

        #region Priority/Ordering Tests

        [Fact]
        public void ProfileMatching_FirstMatchWins_ShouldReturnFirstProfile()
        {
            var config = ConfigWith(
                new LightControlProfile { Name = "Profile1", TargetClientName = "Roku" },
                new LightControlProfile { Name = "Profile2", TargetClientName = "Roku" });

            var profile = Find(config, "Roku", "device1", "192.168.1.100");

            profile.Should().NotBeNull();
            profile!.Name.Should().Be("Profile1");
        }

        [Fact]
        public void ProfileMatching_MoreSpecificFirst_ShouldMatchSpecific()
        {
            var config = ConfigWith(
                new LightControlProfile { Name = "Specific", TargetClientName = "Roku", TargetDeviceIds = new List<string> { "roku-123" } },
                new LightControlProfile { Name = "General", TargetClientName = "Roku" });

            var profile1 = Find(config, "Roku", "roku-123", "192.168.1.100");
            var profile2 = Find(config, "Roku", "roku-456", "192.168.1.100");

            profile1!.Name.Should().Be("Specific");
            profile2!.Name.Should().Be("General");
        }

        #endregion

        #region Semantics the old pasted copy lacked

        [Fact]
        public void FindMatchingProfile_DisabledProfile_IsSkippedWithRejection()
        {
            var config = ConfigWith(
                new LightControlProfile { Name = "Off", Enabled = false },
                new LightControlProfile { Name = "On" });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100"));

            result.Status.Should().Be(MatchStatus.Matched);
            result.Profile!.Name.Should().Be("On");
            var rejection = result.Rejections.Should().ContainSingle().Subject;
            rejection.ProfileName.Should().Be("Off");
            rejection.Filter.Should().Be(RejectionFilter.Disabled);
        }

        [Fact]
        public void FindMatchingProfile_MovieRequest_ProfileWithMoviesOff_IsRejectedOnMediaType()
        {
            var config = ConfigWith(new LightControlProfile { Name = "TV only", EnableForMovies = false, EnableForTvShows = true });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100", isMovie: true));

            result.Status.Should().Be(MatchStatus.NoMatch);
            result.Profile.Should().BeNull();
            var rejection = result.Rejections.Should().ContainSingle().Subject;
            rejection.Filter.Should().Be(RejectionFilter.MediaType);
            rejection.Actual.Should().Be("Movie");
            rejection.Expected.Should().Be("TvShows");
        }

        [Fact]
        public void FindMatchingProfile_EpisodeRequest_ProfileWithTvOn_Matches()
        {
            var config = ConfigWith(new LightControlProfile { Name = "TV", EnableForMovies = false, EnableForTvShows = true });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100", isMovie: false, isEpisode: true));

            result.Status.Should().Be(MatchStatus.Matched);
            result.Profile!.Name.Should().Be("TV");
        }

        [Fact]
        public void FindMatchingProfile_UnknownMediaType_NoMatch()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Both", EnableForMovies = true, EnableForTvShows = true });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100", isMovie: false, isEpisode: false));

            result.Status.Should().Be(MatchStatus.NoMatch);
            result.Rejections.Should().ContainSingle().Which.Actual.Should().Be("Unknown");
            result.Rejections[0].Expected.Should().Be("Movies, TvShows");
        }

        [Fact]
        public void FindMatchingProfile_NoEnabledTypes_ExpectedIsNone()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Neither", EnableForMovies = false, EnableForTvShows = false });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100"));

            result.Rejections.Should().ContainSingle().Which.Expected.Should().Be("none");
        }

        [Fact]
        public void FindMatchingProfile_PluginDisabled_ReturnsPluginDisabledStatus()
        {
            var config = new PluginConfiguration { EnablePlugin = false, Profiles = new List<LightControlProfile> { new LightControlProfile { Name = "P" } } };

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.100"));

            result.Status.Should().Be(MatchStatus.PluginDisabled);
            result.Profile.Should().BeNull();
            result.Rejections.Should().BeEmpty();
        }

        [Fact]
        public void FindMatchingProfile_NoProfiles_ReturnsNoProfilesStatus()
        {
            var result = ProfileMatcher.FindMatchingProfile(ConfigWith(), Req("Roku", "device1", "192.168.1.100"));

            result.Status.Should().Be(MatchStatus.NoProfiles);
            result.Rejections.Should().BeEmpty();
        }

        [Fact]
        public void FindMatchingProfile_ClientNameMismatch_RejectionCarriesValues()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Living Room", TargetClientName = "Roku" });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Jellyfin Web", "device1", "192.168.1.100"));

            var rejection = result.Rejections.Should().ContainSingle().Subject;
            rejection.ProfileName.Should().Be("Living Room");
            rejection.Filter.Should().Be(RejectionFilter.ClientName);
            rejection.Actual.Should().Be("Jellyfin Web");
            rejection.Expected.Should().Be("Roku");
        }

        [Fact]
        public void FindMatchingProfile_IpMismatch_RejectionCarriesExtractedIp()
        {
            var config = ConfigWith(new LightControlProfile { Name = "IP", TargetIpAddress = "192.168.1.9" });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "device1", "192.168.1.5:8096"));

            var rejection = result.Rejections.Should().ContainSingle().Subject;
            rejection.Filter.Should().Be(RejectionFilter.IpAddress);
            rejection.Actual.Should().Be("192.168.1.5");
            rejection.Expected.Should().Be("192.168.1.9");
        }

        [Fact]
        public void FindMatchingProfile_DeviceMismatch_RejectionListsConfiguredIds()
        {
            var config = ConfigWith(new LightControlProfile { Name = "Dev", TargetDeviceIds = new List<string> { "a", "b" } });

            var result = ProfileMatcher.FindMatchingProfile(config, Req("Roku", "zzz", "192.168.1.5"));

            var rejection = result.Rejections.Should().ContainSingle().Subject;
            rejection.Filter.Should().Be(RejectionFilter.DeviceId);
            rejection.Actual.Should().Be("zzz");
            rejection.Expected.Should().Be("a, b");
        }

        [Theory]
        [InlineData(true, false, true, false, true)]   // movie, movies enabled
        [InlineData(true, false, false, true, false)]  // movie, only TV enabled
        [InlineData(false, true, false, true, true)]   // episode, TV enabled
        [InlineData(false, true, true, false, false)]  // episode, only movies enabled
        [InlineData(false, false, true, true, false)]  // unknown never matches
        public void MediaTypeMatches_FollowsProfileFlags(bool isMovie, bool isEpisode, bool moviesOn, bool tvOn, bool expected)
        {
            var profile = new LightControlProfile { EnableForMovies = moviesOn, EnableForTvShows = tvOn };

            ProfileMatcher.MediaTypeMatches(profile, isMovie, isEpisode).Should().Be(expected);
        }

        #endregion

        #region ExtractIpAddress (moved from HueServiceTests)

        [Theory]
        [InlineData("192.168.1.100", "192.168.1.100")]
        [InlineData("192.168.1.100:12345", "192.168.1.100")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void ExtractIpAddress_ShouldRemovePort(string input, string expected)
        {
            ProfileMatcher.ExtractIpAddress(input).Should().Be(expected);
        }

        #endregion
    }
}
