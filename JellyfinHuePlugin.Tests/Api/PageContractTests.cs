using System;
using System.Linq;
using FluentAssertions;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Api
{
    /// <summary>
    /// The property names configPage.html reads from each endpoint's JSON, pinned by reflection
    /// against the type the endpoint returns. Renaming one of these compiles and passes every
    /// other test; this is the test that fails. Extracted from the page on 2026-09-13 - update
    /// both together.
    /// </summary>
    public class PageContractTests
    {
        public static TheoryData<Type, string[]> Contract => new()
        {
            { typeof(HueBridgeDiscovery), new[] { "Id", "InternalIpAddress" } },                                   // discover
            { typeof(AuthenticationResult), new[] { "Success", "Username", "Id", "HardwareId", "Error" } },        // authenticate
            { typeof(VerifyConnectionResult), new[] { "Success", "Error", "HardwareId", "ModelId", "SoftwareVersion" } }, // verifyconnection
            { typeof(MigrationReport), new[] { "Changed", "Bridges", "Profiles" } },                                // migrate
            { typeof(ProfileMigration), new[] { "Rewritten", "Unresolved" } },
            { typeof(BridgeMigration), new[] { "Status", "Name" } },
            { typeof(HueGroupResource), new[] { "Name", "Type" } },                                                 // groups (dictionary values)
            { typeof(HueSceneResource), new[] { "Name", "GroupName" } },                                            // scenes (dictionary values)
            { typeof(BridgeInfo), new[] { "Id", "Name", "IpAddress", "IsAuthenticated" } }                          // bridges
        };

        [Theory]
        [MemberData(nameof(Contract))]
        public void ThePageReadsOnlyPropertiesThatExist(Type dto, string[] names)
        {
            var actual = dto.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            names.Should().BeSubsetOf(actual, because: "configPage.html reads these from {0}", dto.Name);
        }

        [Fact]
        public void GroupsAreKeyedByGroupedLightId()
        {
            // The dictionary key is what a profile stores as TargetGroupId; HueControllerTests
            // proves the keying, this pins the property the key comes from still exists.
            typeof(HueGroupResource).GetProperty("GroupedLightId").Should().NotBeNull();
        }

        [Fact]
        public void MigrationStatusValuesThePageComparesAgainstExist()
        {
            MigrationStatus.Unreachable.Should().Be("unreachable");
            MigrationStatus.Ok.Should().Be("ok");
        }
    }
}
