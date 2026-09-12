using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// Captures the exact JSON the service sends to the bridge, and feeds back
    /// canned bridge responses. The Hue v1 API keys are all lowercase and the
    /// bridge reports per-key errors inside an HTTP 200 array.
    /// </summary>
    public class HueServiceRequestTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly string _responseBody;
            private readonly HttpStatusCode _status;

            public CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
            {
                _responseBody = responseBody;
                _status = status;
            }

            public string? LastBody { get; private set; }
            public string? LastUrl { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUrl = request.RequestUri?.ToString();
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
                };
            }
        }

        private const string AllSuccess = @"[{""success"":{""/groups/0/action/on"":true}},{""success"":{""/groups/0/action/bri"":20}},{""success"":{""/groups/0/action/transitiontime"":30}}]";

        private static (HueService service, CapturingHandler handler) Make(string response = AllSuccess, HttpStatusCode status = HttpStatusCode.OK)
        {
            var handler = new CapturingHandler(response, status);
            return (new HueService(NullLogger<HueService>.Instance, handler), handler);
        }

        private static Dictionary<string, JsonElement> ParseBody(string? body)
        {
            body.Should().NotBeNull();
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body!)!;
        }

        [Fact]
        public async Task SetGroupState_ShouldSendLowercaseHueKeys()
        {
            var (service, handler) = Make();

            var ok = await service.SetGroupStateAsync("192.168.1.2", "user", "0",
                new HueLightState { On = true, Bri = 20, TransitionTime = 30 });

            ok.Should().BeTrue();
            var body = ParseBody(handler.LastBody);
            body.Keys.Should().BeEquivalentTo(new[] { "on", "bri", "transitiontime" });
            body["on"].GetBoolean().Should().BeTrue();
            body["bri"].GetInt32().Should().Be(20);
            body["transitiontime"].GetInt32().Should().Be(30);
        }

        [Fact]
        public async Task SetGroupState_ShouldOmitNullFields()
        {
            var (service, handler) = Make();

            await service.SetGroupStateAsync("192.168.1.2", "user", "0", new HueLightState { On = false });

            var body = ParseBody(handler.LastBody);
            body.Keys.Should().BeEquivalentTo(new[] { "on" });
        }

        [Fact]
        public async Task SetLightState_ShouldSendLowercaseHueKeys()
        {
            var (service, handler) = Make();

            await service.SetLightStateAsync("192.168.1.2", "user", "3",
                new HueLightState { On = true, Bri = 100, TransitionTime = 5 });

            handler.LastUrl.Should().Be("https://192.168.1.2/api/user/lights/3/state");
            var body = ParseBody(handler.LastBody);
            body.Keys.Should().BeEquivalentTo(new[] { "on", "bri", "transitiontime" });
        }

        [Fact]
        public async Task ActivateScene_ShouldSendSceneAndTransitionTime()
        {
            var (service, handler) = Make(@"[{""success"":{""/groups/1/action/scene"":""abc""}}]");

            var ok = await service.ActivateSceneAsync("192.168.1.2", "user", "1", "abc", 15);

            ok.Should().BeTrue();
            var body = ParseBody(handler.LastBody);
            body.Keys.Should().BeEquivalentTo(new[] { "scene", "transitiontime" });
            body["transitiontime"].GetInt32().Should().Be(15);
        }

        [Fact]
        public async Task SetGroupState_ShouldReturnFalse_WhenBridgeReportsErrorInsideHttp200()
        {
            // Real bridge shape for an unknown parameter: HTTP 200, error entry in the array.
            var response = @"[{""success"":{""/groups/0/action/on"":true}},{""error"":{""type"":6,""address"":""/groups/0/action/transitionTime"",""description"":""parameter, transitionTime, not available""}}]";
            var (service, _) = Make(response);

            var ok = await service.SetGroupStateAsync("192.168.1.2", "user", "0", new HueLightState { On = true });

            ok.Should().BeFalse();
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound, @"[{""error"":{""type"":3,""address"":""/groups/9/action"",""description"":""resource not available""}}]")]
        [InlineData(HttpStatusCode.OK, "<html><body>Not a bridge</body></html>")]
        [InlineData(HttpStatusCode.OK, "")]
        [InlineData(HttpStatusCode.OK, "null")]
        [InlineData(HttpStatusCode.OK, @"{""success"":true}")]
        public async Task SetGroupState_ShouldReturnFalse_OnNonArrayOrNonSuccessResponse(HttpStatusCode status, string body)
        {
            var (service, _) = Make(body, status);

            var ok = await service.SetGroupStateAsync("192.168.1.2", "user", "0", new HueLightState { On = true });

            ok.Should().BeFalse();
        }

        [Fact]
        public async Task SetGroupState_ShouldReturnTrue_OnEmptyArray()
        {
            var (service, _) = Make("[]");

            var ok = await service.SetGroupStateAsync("192.168.1.2", "user", "0", new HueLightState { On = true });

            ok.Should().BeTrue();
        }

        [Fact]
        public async Task SetGroupState_ShouldReturnFalse_OnUnauthorizedUserError()
        {
            var response = @"[{""error"":{""type"":1,""address"":""/groups/0/action"",""description"":""unauthorized user""}}]";
            var (service, _) = Make(response);

            var ok = await service.SetGroupStateAsync("192.168.1.2", "bad", "0", new HueLightState { On = true });

            ok.Should().BeFalse();
        }
    }
}
