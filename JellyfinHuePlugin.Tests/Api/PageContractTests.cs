using System;
using System.Linq;
using System.Text.Json;
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
            { typeof(BridgeInfo), new[] { "Id", "Name", "IpAddress", "IsAuthenticated" } },                         // bridges
            { typeof(TestLightResult), new[] { "Success", "Error" } },                                              // test
        };

        [Theory]
        [MemberData(nameof(Contract))]
        public void ThePageReadsOnlyPropertiesThatExist(Type dto, string[] names)
        {
            var actual = dto.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            names.Should().BeSubsetOf(actual, because: "configPage.html reads these from {0}", dto.Name);
        }

        [Theory]
        [MemberData(nameof(Contract))]
        public void TheSerializedJsonCarriesThoseNames(Type dto, string[] names)
        {
            // Reflection alone would miss a [JsonPropertyName] renaming the wire name; the page
            // reads the JSON, so the JSON is what is pinned. Same defaults the controller uses.
            var json = JsonSerializer.Serialize(Instantiate(dto), dto);

            using var document = JsonDocument.Parse(json);
            var actual = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            names.Should().BeSubsetOf(actual, because: "configPage.html reads these from {0}'s JSON", dto.Name);
        }

        /// <summary>A default instance: parameterless where there is one, otherwise dummy arguments.</summary>
        private static object Instantiate(Type type)
        {
            var parameterless = type.GetConstructor(Type.EmptyTypes);
            if (parameterless != null)
            {
                return parameterless.Invoke(null);
            }

            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var arguments = constructor.GetParameters()
                .Select(p => p.ParameterType == typeof(string) ? "x" : DefaultOf(p.ParameterType))
                .ToArray();
            return constructor.Invoke(arguments);
        }

        private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

        [Fact]
        public void GroupsAreKeyedByGroupedLightId()
        {
            // The dictionary key is what a profile stores as TargetGroupId; HueControllerTests
            // proves the keying, this pins the property the key comes from still exists.
            typeof(HueGroupResource).GetProperty("GroupedLightId").Should().NotBeNull();
        }

        [Fact]
        public void TheTestRequestThePageSendsDeserializes()
        {
            // The page sends the action by name; LightAction carries its own converter, so this
            // holds with bare defaults as well as under the host's options.
            var request = JsonSerializer.Deserialize<TestLightRequest>(@"{""Action"":""Pause"",""Profile"":{""Name"":""x"",""PlaySceneId"":""""}}")!;

            request.Action.Should().Be(LightAction.Pause);
            request.Profile!.Name.Should().Be("x");
            request.Profile.PlaySceneId.Should().BeEmpty();
        }

        [Fact]
        public void MigrationStatusValuesThePageComparesAgainstExist()
        {
            MigrationStatus.Unreachable.Should().Be("unreachable");
            MigrationStatus.Ok.Should().Be("ok");
        }
    }
}
