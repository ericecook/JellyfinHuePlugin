using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// CLIP v2 wire shapes through the handler seam: paths, the application-key header, the
    /// expected-bridge-id option, request bodies, envelope handling and input validation.
    /// </summary>
    public class HueServiceV2Tests
    {
        private const string BridgeId = "001788fffe123456";
        private const string Key = "Qn74cB7YlKursSzMYyPL4pr5oLWxayBqhKyjFD10";
        private const string ConfigBody = @"{""name"":""Hue"",""swversion"":""1968004000"",""apiversion"":""1.68.0"",""modelid"":""BSB002"",""bridgeid"":""001788FFFE123456""}";
        private const string Empty = @"{""errors"":[],""data"":[]}";

        private sealed record Captured(HttpMethod Method, string Path, string? Header, bool HasOption, string? ExpectedBridgeId, string? Body);

        private sealed class RoutingHandler : HttpMessageHandler
        {
            public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new();
            public List<Captured> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                var hasOption = request.Options.TryGetValue(HueService.ExpectedBridgeIdOption, out var expected);
                var header = request.Headers.TryGetValues("hue-application-key", out var values) ? values.First() : null;
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add(new Captured(request.Method, path, header, hasOption, expected, body));

                var (status, responseBody) = Responses.TryGetValue(path, out var r) ? r : (HttpStatusCode.NotFound, @"{""errors"":[{""description"":""not found""}],""data"":[]}");
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class CapturingLogger : ILogger<HueService>
        {
            public List<string> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }

        private readonly RoutingHandler _handler = new();
        private readonly CapturingLogger _log = new();
        private readonly HueService _service;

        public HueServiceV2Tests()
        {
            _service = new HueService(_log, _handler);
            _handler.Responses["/api/0/config"] = (HttpStatusCode.OK, ConfigBody);
        }

        private static HueBridge Bridge(string bridgeId = BridgeId, string ip = "192.168.1.50", string key = Key) =>
            new() { Id = "bridge1", Name = "Test Bridge", IpAddress = ip, Username = key, HardwareId = bridgeId };

        private static JsonElement Json(string? body) => JsonDocument.Parse(body ?? "null").RootElement;

        [Fact]
        public async Task GetBridgeInfo_ParsesConfigAndLowersTheId()
        {
            var info = await _service.GetBridgeInfoAsync("https://192.168.1.50/");

            info.Should().NotBeNull();
            info!.HardwareId.Should().Be(BridgeId);
            info.SoftwareVersion.Should().Be("1968004000");
            info.ModelId.Should().Be("BSB002");
            info.SupportsV2.Should().BeTrue();
            var request = _handler.Requests.Single();
            request.Path.Should().Be("/api/0/config");
            request.Header.Should().BeNull();
            request.HasOption.Should().BeTrue();
            // Empty, not null: see the comment on NewRequest's Options.Set call in HueService --
            // HttpRequestOptions.TryGetValue can never report "present" for a stored null.
            request.ExpectedBridgeId.Should().BeEmpty();
        }

        [Theory]
        [InlineData("1948086000", true)]
        [InlineData("1948085999", false)]
        [InlineData("", false)]
        public async Task GetBridgeInfo_SupportsV2_FollowsTheSoftwareFloor(string swversion, bool expected)
        {
            _handler.Responses["/api/0/config"] = (HttpStatusCode.OK, $@"{{""swversion"":""{swversion}"",""bridgeid"":""{BridgeId}""}}");

            var info = await _service.GetBridgeInfoAsync("192.168.1.50");

            info!.SupportsV2.Should().Be(expected);
        }

        [Fact]
        public async Task GetBridgeInfo_InvalidAddress_SendsNothing()
        {
            var info = await _service.GetBridgeInfoAsync("user@host");

            info.Should().BeNull();
            _handler.Requests.Should().BeEmpty();
            _log.Lines.Should().ContainSingle(l => l.Contains("is not a valid host"));
        }

        [Fact]
        public async Task Authenticate_Success_ReturnsKeyAndHardwareId_PinsTheId_NeverLogsTheKey()
        {
            _handler.Responses["/api"] = (HttpStatusCode.OK, $@"[{{""success"":{{""username"":""{Key}""}}}}]");

            var outcome = await _service.AuthenticateAsync("192.168.1.50");

            outcome.Should().Be(new AuthenticationOutcome(Key, BridgeId));
            var post = _handler.Requests.Single(r => r.Path == "/api");
            post.Method.Should().Be(HttpMethod.Post);
            post.ExpectedBridgeId.Should().Be(BridgeId);
            post.Header.Should().BeNull();
            Json(post.Body).GetProperty("devicetype").GetString().Should().Be("jellyfin_hue_plugin");
            Json(post.Body).GetProperty("generateclientkey").GetBoolean().Should().BeFalse();
            _log.Lines.Should().NotContain(l => l.Contains(Key));
            _log.Lines.Should().Contain(l => l.Contains("Successfully authenticated with bridge " + BridgeId));
        }

        [Fact]
        public async Task Authenticate_LinkButtonNotPressed_ReturnsNull()
        {
            _handler.Responses["/api"] = (HttpStatusCode.OK, @"[{""error"":{""type"":101,""address"":"""",""description"":""link button not pressed""}}]");

            var outcome = await _service.AuthenticateAsync("192.168.1.50");

            outcome.Should().BeNull();
            _log.Lines.Should().Contain(l => l.Contains("Authentication failed") && l.Contains("101"));
        }

        [Fact]
        public async Task Authenticate_UnsupportedBridge_ReturnsNullWithoutPosting()
        {
            _handler.Responses["/api/0/config"] = (HttpStatusCode.OK, $@"{{""swversion"":""1940000000"",""bridgeid"":""{BridgeId}""}}");

            var outcome = await _service.AuthenticateAsync("192.168.1.50");

            outcome.Should().BeNull();
            _handler.Requests.Should().NotContain(r => r.Path == "/api");
            _log.Lines.Should().Contain(l => l.Contains("not a supported v2 bridge"));
        }

        [Fact]
        public async Task SetGroupedLight_SendsHeaderPathOptionAndBody()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, Empty);

            var ok = await _service.SetGroupedLightAsync(Bridge(), "gl-1", new GroupedLightState { On = true, Brightness = 20, DurationMs = 400 });

            ok.Should().BeTrue();
            var put = _handler.Requests.Single();
            put.Method.Should().Be(HttpMethod.Put);
            put.Path.Should().Be("/clip/v2/resource/grouped_light/gl-1");
            put.Header.Should().Be(Key);
            put.ExpectedBridgeId.Should().Be(BridgeId);
            var body = Json(put.Body);
            body.GetProperty("on").GetProperty("on").GetBoolean().Should().BeTrue();
            body.GetProperty("dimming").GetProperty("brightness").GetDouble().Should().Be(20);
            body.GetProperty("dynamics").GetProperty("duration").GetInt32().Should().Be(400);
        }

        [Fact]
        public async Task SetGroupedLight_OffWithDuration_SendsOnlyThoseFields()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, Empty);

            await _service.SetGroupedLightAsync(Bridge(), "gl-1", new GroupedLightState { On = false, DurationMs = 1500 });

            var body = Json(_handler.Requests.Single().Body);
            body.GetProperty("on").GetProperty("on").GetBoolean().Should().BeFalse();
            body.GetProperty("dynamics").GetProperty("duration").GetInt32().Should().Be(1500);
            body.TryGetProperty("dimming", out _).Should().BeFalse();
        }

        [Fact]
        public async Task SetGroupedLight_BrightnessOnly_OmitsOnAndDynamics()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, Empty);

            await _service.SetGroupedLightAsync(Bridge(), "gl-1", new GroupedLightState { Brightness = 55.5 });

            var body = Json(_handler.Requests.Single().Body);
            body.GetProperty("dimming").GetProperty("brightness").GetDouble().Should().Be(55.5);
            body.TryGetProperty("on", out _).Should().BeFalse();
            body.TryGetProperty("dynamics", out _).Should().BeFalse();
        }

        [Fact]
        public async Task SetGroupedLight_EnvelopeError_ReturnsFalseAndWarns()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, @"{""errors"":[{""description"":""invalid value""}],""data"":[]}");

            var ok = await _service.SetGroupedLightAsync(Bridge(), "gl-1", new GroupedLightState { On = true });

            ok.Should().BeFalse();
            _log.Lines.Should().Contain("Bridge error for grouped_light gl-1: invalid value");
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden, @"{""errors"":[{""description"":""unauthorized user""}],""data"":[]}")]
        [InlineData(HttpStatusCode.OK, "<html>not a bridge</html>")]
        [InlineData(HttpStatusCode.OK, "[]")]
        [InlineData(HttpStatusCode.OK, @"{""data"":""nope""}")]
        public async Task SetGroupedLight_NonSuccessOrMalformed_ReturnsFalse(HttpStatusCode status, string body)
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (status, body);

            var ok = await _service.SetGroupedLightAsync(Bridge(), "gl-1", new GroupedLightState { On = true });

            ok.Should().BeFalse();
        }

        [Fact]
        public async Task SetGroupedLight_InvalidKey_SendsNothingAndNeverLogsIt()
        {
            var ok = await _service.SetGroupedLightAsync(Bridge(key: "bad/key"), "gl-1", new GroupedLightState { On = true });

            ok.Should().BeFalse();
            _handler.Requests.Should().BeEmpty();
            _log.Lines.Should().ContainSingle(l => l.Contains("API key is not valid"));
            _log.Lines.Should().NotContain(l => l.Contains("bad/key"));
        }

        [Fact]
        public async Task SetGroupedLight_InvalidGroupedLightId_SendsNothing()
        {
            var ok = await _service.SetGroupedLightAsync(Bridge(), "gl/../1", new GroupedLightState { On = true });

            ok.Should().BeFalse();
            _handler.Requests.Should().BeEmpty();
        }

        [Fact]
        public async Task SetGroupedLight_ConfiguredHardwareId_SkipsTheConfigCall()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, Empty);

            await _service.SetGroupedLightAsync(Bridge(bridgeId: "001788FFFE123456"), "gl-1", new GroupedLightState { On = true });

            _handler.Requests.Should().ContainSingle().Which.ExpectedBridgeId.Should().Be(BridgeId);
        }

        [Fact]
        public async Task SetGroupedLight_EmptyHardwareId_LearnsItOnceAndPins()
        {
            _handler.Responses["/clip/v2/resource/grouped_light/gl-1"] = (HttpStatusCode.OK, Empty);

            await _service.SetGroupedLightAsync(Bridge(bridgeId: ""), "gl-1", new GroupedLightState { On = true });
            await _service.SetGroupedLightAsync(Bridge(bridgeId: ""), "gl-1", new GroupedLightState { On = false });

            _handler.Requests.Count(r => r.Path == "/api/0/config").Should().Be(1);
            _handler.Requests.Where(r => r.Path.StartsWith("/clip")).Should().OnlyContain(r => r.ExpectedBridgeId == BridgeId);
        }

        [Fact]
        public async Task SetGroupedLight_EmptyHardwareId_UnsupportedBridge_SendsNothingToClip()
        {
            _handler.Responses["/api/0/config"] = (HttpStatusCode.OK, $@"{{""swversion"":""1940000000"",""bridgeid"":""{BridgeId}""}}");

            var ok = await _service.SetGroupedLightAsync(Bridge(bridgeId: ""), "gl-1", new GroupedLightState { On = true });

            ok.Should().BeFalse();
            _handler.Requests.Should().NotContain(r => r.Path.StartsWith("/clip"));
        }

        [Fact]
        public async Task RecallScene_SendsRecallWithDuration()
        {
            _handler.Responses["/clip/v2/resource/scene/sc-1"] = (HttpStatusCode.OK, Empty);

            var ok = await _service.RecallSceneAsync(Bridge(), "sc-1", 400);

            ok.Should().BeTrue();
            var put = _handler.Requests.Single();
            put.Path.Should().Be("/clip/v2/resource/scene/sc-1");
            put.Header.Should().Be(Key);
            var recall = Json(put.Body).GetProperty("recall");
            recall.GetProperty("action").GetString().Should().Be("active");
            recall.GetProperty("duration").GetInt32().Should().Be(400);
        }

        [Fact]
        public async Task RecallScene_WithoutDuration_OmitsIt()
        {
            _handler.Responses["/clip/v2/resource/scene/sc-1"] = (HttpStatusCode.OK, Empty);

            await _service.RecallSceneAsync(Bridge(), "sc-1", null);

            Json(_handler.Requests.Single().Body).GetProperty("recall").TryGetProperty("duration", out _).Should().BeFalse();
        }

        [Fact]
        public async Task GetGroups_MergesRoomsZonesAndBridgeHome()
        {
            _handler.Responses["/clip/v2/resource/room"] = (HttpStatusCode.OK,
                @"{""errors"":[],""data"":[{""id"":""room-1"",""id_v1"":""/groups/1"",""metadata"":{""name"":""Theater""},""services"":[{""rid"":""dev-1"",""rtype"":""device""},{""rid"":""gl-1"",""rtype"":""grouped_light""}]}]}");
            _handler.Responses["/clip/v2/resource/zone"] = (HttpStatusCode.OK,
                @"{""errors"":[],""data"":[{""id"":""zone-1"",""id_v1"":""/groups/5"",""metadata"":{""name"":""Downstairs""},""services"":[{""rid"":""gl-5"",""rtype"":""grouped_light""}]}]}");
            _handler.Responses["/clip/v2/resource/bridge_home"] = (HttpStatusCode.OK,
                @"{""errors"":[],""data"":[{""id"":""home-1"",""id_v1"":""/groups/0"",""services"":[{""rid"":""gl-0"",""rtype"":""grouped_light""}]}]}");

            var groups = await _service.GetGroupsAsync(Bridge());

            groups.Should().BeEquivalentTo(new[]
            {
                new HueGroupResource("room-1", "gl-1", "Theater", "room", "/groups/1"),
                new HueGroupResource("zone-1", "gl-5", "Downstairs", "zone", "/groups/5"),
                new HueGroupResource("home-1", "gl-0", "All Lights", "bridge_home", "/groups/0")
            });
            _handler.Requests.Should().OnlyContain(r => r.Header == Key && r.ExpectedBridgeId == BridgeId);
        }

        [Fact]
        public async Task GetGroups_AnyFailure_ReturnsNull()
        {
            _handler.Responses["/clip/v2/resource/room"] = (HttpStatusCode.OK, Empty);
            // zone is unrouted → 404

            var groups = await _service.GetGroupsAsync(Bridge());

            groups.Should().BeNull();
        }

        [Fact]
        public async Task GetScenes_ParsesGroupAndIdV1()
        {
            _handler.Responses["/clip/v2/resource/scene"] = (HttpStatusCode.OK,
                @"{""errors"":[],""data"":[{""id"":""sc-1"",""id_v1"":""/scenes/abc123"",""metadata"":{""name"":""Movie""},""group"":{""rid"":""room-1"",""rtype"":""room""}}]}");

            var scenes = await _service.GetScenesAsync(Bridge());

            scenes.Should().ContainSingle().Which.Should().Be(new HueSceneResource("sc-1", "Movie", "room-1", "/scenes/abc123"));
        }

        [Fact]
        public async Task GetLights_ParsesOnAndBrightness()
        {
            _handler.Responses["/clip/v2/resource/light"] = (HttpStatusCode.OK,
                @"{""errors"":[],""data"":[{""id"":""l-1"",""id_v1"":""/lights/8"",""metadata"":{""name"":""Lamp""},""on"":{""on"":true},""dimming"":{""brightness"":42.5}},{""id"":""l-2"",""metadata"":{""name"":""Plug""},""on"":{""on"":false}}]}");

            var lights = await _service.GetLightsAsync(Bridge());

            lights.Should().BeEquivalentTo(new[]
            {
                new HueLightResource("l-1", "Lamp", true, 42.5, "/lights/8"),
                new HueLightResource("l-2", "Plug", false, null, null)
            });
        }

        [Fact]
        public async Task TestBridgeConnection_DescribesTheBridge()
        {
            var text = await _service.TestBridgeConnectionAsync("192.168.1.50");

            text.Should().Contain(BridgeId).And.Contain("BSB002").And.Contain("supports API v2");
        }

        [Fact]
        public async Task CancelledToken_PropagatesFromEveryBridgeMethod()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var bridge = Bridge();

            await FluentActions.Awaiting(() => _service.GetBridgeInfoAsync("192.168.1.50", cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.AuthenticateAsync("192.168.1.50", cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.GetGroupsAsync(bridge, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.GetScenesAsync(bridge, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.GetLightsAsync(bridge, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.SetGroupedLightAsync(bridge, "gl-1", new GroupedLightState { On = true }, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.RecallSceneAsync(bridge, "sc-1", null, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            await FluentActions.Awaiting(() => _service.DiscoverBridgesAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        }

        private const string CloudBody = @"[{""id"":""001788FFFE123456"",""internalipaddress"":""192.168.1.50""}]";

        private static Mock<MdnsBridgeDiscovery> Mdns(params HueBridgeDiscovery[] answers)
        {
            var mdns = new Mock<MdnsBridgeDiscovery>(NullLogger.Instance);
            mdns.Setup(m => m.DiscoverAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((TimeSpan _, CancellationToken ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(answers.ToList());
                });
            return mdns;
        }

        [Fact]
        public async Task Discover_WhenMdnsAnswers_NeverCallsTheCloud()
        {
            var mdns = Mdns(new HueBridgeDiscovery { Id = "c42996fffec03a49", InternalIpAddress = "192.168.1.170" });
            var service = new HueService(_log, _handler, mdns.Object);
            _handler.Responses["/"] = (HttpStatusCode.OK, CloudBody);

            var found = await service.DiscoverBridgesAsync();

            found.Should().ContainSingle().Which.Id.Should().Be("c42996fffec03a49");
            _handler.Requests.Should().BeEmpty();
            _log.Lines.Should().Contain("Found 1 Hue bridge(s) via mDNS");
        }

        [Fact]
        public async Task Discover_WhenMdnsIsSilent_FallsBackToTheCloud()
        {
            var service = new HueService(_log, _handler, Mdns().Object);
            _handler.Responses["/"] = (HttpStatusCode.OK, CloudBody);

            var found = await service.DiscoverBridgesAsync();

            found.Should().ContainSingle().Which.InternalIpAddress.Should().Be("192.168.1.50");
            _handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/");
            _log.Lines.Should().Contain("No Hue bridge answered mDNS; trying cloud discovery");
        }

        [Fact]
        public async Task Discover_WithoutLocalDiscovery_UsesTheCloud()
        {
            _handler.Responses["/"] = (HttpStatusCode.OK, CloudBody);

            var found = await _service.DiscoverBridgesAsync();

            found.Should().ContainSingle();
            _handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/");
        }

        [Fact]
        public async Task Discover_CancelledDuringMdns_Propagates()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var service = new HueService(_log, _handler, Mdns().Object);

            await FluentActions.Awaiting(() => service.DiscoverBridgesAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
            _handler.Requests.Should().BeEmpty();
        }
    }
}
