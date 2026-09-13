using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    public class HueService : IDisposable
    {
        /// <summary>Names a PEM file whose certificate is trusted as an extra bridge root. Test-only.</summary>
        public const string ExtraRootEnvironmentVariable = "JELLYFIN_HUE_EXTRA_ROOT_PEM";

        /// <summary>Bridge software from which CLIP v2 is available.</summary>
        internal const long MinimumV2SoftwareVersion = 1948086000;

        /// <summary>
        /// Present on every bridge request. Its value is the bridge id the certificate must name,
        /// or null for the one unauthenticated call that learns the id (chain validation only).
        /// Absent on requests to anything that is not a bridge, which then get system trust.
        /// </summary>
        internal static readonly HttpRequestOptionsKey<string?> ExpectedBridgeIdOption = new("hue.expectedBridgeId");

        private const string ApplicationKeyHeader = "hue-application-key";
        private const string DeviceType = "jellyfin_hue_plugin";

        private readonly ILogger<HueService> _logger;
        private readonly HttpClient _httpClient;

        /// <summary>Bridge ids learned from /api/0/config for bridges whose configuration has none yet.</summary>
        private readonly ConcurrentDictionary<string, string> _bridgeIdsByHost = new(StringComparer.OrdinalIgnoreCase);

        // Web defaults (camelCase, which the v2 keys already are), nulls omitted.
        private static readonly JsonSerializerOptions HueJsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public HueService(ILogger<HueService> logger)
            : this(logger, CreateDefaultHandler(logger, BridgeCertificateValidator.LoadRoots(Environment.GetEnvironmentVariable(ExtraRootEnvironmentVariable), logger)))
        {
        }

        // Test seam: lets tests capture requests and feed canned bridge responses.
        public HueService(ILogger<HueService> logger, HttpMessageHandler handler)
        {
            _logger = logger;
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        // One client for everything. Bridge requests carry ExpectedBridgeIdOption and are judged by
        // BridgeCertificateValidator; anything else (the cloud discovery endpoint) needs system trust.
        private static HttpMessageHandler CreateDefaultHandler(ILogger logger, IReadOnlyList<X509Certificate2> roots) => new HttpClientHandler
        {
            AllowAutoRedirect = false, // Prevent HTTP->HTTPS redirects
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (!request.Options.TryGetValue(ExpectedBridgeIdOption, out var expectedBridgeId))
                {
                    return errors == SslPolicyErrors.None;
                }

                if (certificate == null)
                {
                    return false;
                }

                var verdict = BridgeCertificateValidator.Validate(certificate, string.IsNullOrEmpty(expectedBridgeId) ? null : expectedBridgeId, roots);
                if (verdict != BridgeCertificateValidator.Verdict.Trusted)
                {
                    logger.LogWarning("Bridge certificate rejected for {Host}: {Verdict} (subject {Subject}, expected {BridgeId})",
                        request.RequestUri?.Host, verdict, BridgeCertificateValidator.SubjectCommonName(certificate), string.IsNullOrEmpty(expectedBridgeId) ? "any" : expectedBridgeId);
                }

                return verdict == BridgeCertificateValidator.Verdict.Trusted;
            }
        };

        // ---------------------------------------------------------------------------------
        // CLIP v2
        // ---------------------------------------------------------------------------------

        private HttpRequestMessage NewRequest(HttpMethod method, Uri uri, string? applicationKey, string? expectedBridgeId, object? body)
        {
            var request = new HttpRequestMessage(method, uri);
            // HttpRequestOptions.TryGetValue<string?> reports "absent" whenever the stored value is a
            // literal null (it pattern-matches the boxed value against TValue, which never matches null
            // for a reference type) -- indistinguishable from a request that never called NewRequest at
            // all. Store an empty sentinel instead so every bridge request is recognized as one; the
            // certificate callback below treats empty the same as "no specific id" (chain-only).
            request.Options.Set(ExpectedBridgeIdOption, expectedBridgeId ?? string.Empty);
            if (applicationKey != null)
            {
                request.Headers.TryAddWithoutValidation(ApplicationKeyHeader, applicationKey);
            }

            if (body != null)
            {
                request.Content = JsonContent.Create(body, options: HueJsonOptions);
            }

            return request;
        }

        /// <summary>Validates the bridge's address and key and pins the bridge id. Null after one warning.</summary>
        private async Task<(string Host, string BridgeId)?> PrepareAsync(HueBridge bridge, CancellationToken cancellationToken)
        {
            if (!BridgeUri.TryParseHost(bridge.IpAddress, out var host))
            {
                _logger.LogWarning("Bridge address {Address} is not a valid host", bridge.IpAddress);
                return null;
            }

            if (!BridgeUri.IsValidSegment(bridge.Username))
            {
                _logger.LogWarning("Bridge {Host} API key is not valid", host);
                return null;
            }

            var bridgeId = await ResolveBridgeIdAsync(host, bridge.BridgeId, cancellationToken);
            return bridgeId == null ? null : (host, bridgeId);
        }

        private async Task<string?> ResolveBridgeIdAsync(string host, string? configuredBridgeId, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(configuredBridgeId))
            {
                return configuredBridgeId.Trim().ToLowerInvariant();
            }

            if (_bridgeIdsByHost.TryGetValue(host, out var cached))
            {
                return cached;
            }

            var info = await GetBridgeInfoAsync(host, cancellationToken);
            if (info == null)
            {
                return null;
            }

            if (!info.SupportsV2)
            {
                _logger.LogWarning("Bridge {Host} is not a supported v2 bridge (software {Version})", host, info.SoftwareVersion);
                return null;
            }

            _bridgeIdsByHost[host] = info.BridgeId;
            return info.BridgeId;
        }

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var name in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        private static string? FindService(JsonElement resource, string rtype)
        {
            if (!resource.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var service in services.EnumerateArray())
            {
                if (GetString(service, "rtype") == rtype)
                {
                    return GetString(service, "rid");
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the v2 envelope. Returns the data array (cloned, so the document can be disposed)
        /// when the status is 2xx and errors is empty; logs every error description and returns
        /// null otherwise.
        /// </summary>
        private async Task<JsonElement?> ReadEnvelopeAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            // A wrong IP can serve a whole web page; keep log lines bounded.
            var excerpt = content.Length > 500 ? content[..500] + "…" : content;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bridge returned {Status} for {Operation}: {Content}", response.StatusCode, operation, excerpt);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", operation, excerpt);
                    return null;
                }

                var failed = false;
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var error in errors.EnumerateArray())
                    {
                        failed = true;
                        _logger.LogWarning("Bridge error for {Operation}: {Description}", operation, GetString(error, "description") ?? "?");
                    }
                }

                if (failed)
                {
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", operation, excerpt);
                    return null;
                }

                return data.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}: {Content}", operation, excerpt);
                return null;
            }
        }

        private async Task<JsonElement?> GetResourcesAsync(HueBridge bridge, string resourceType, CancellationToken cancellationToken)
        {
            var target = await PrepareAsync(bridge, cancellationToken);
            if (target == null)
            {
                return null;
            }

            var uri = BridgeUri.Build(target.Value.Host, "clip", "v2", "resource", resourceType);
            var response = await SendWithRetryAsync(() => _httpClient.SendAsync(
                NewRequest(HttpMethod.Get, uri, bridge.Username, target.Value.BridgeId, null), cancellationToken));
            return await ReadEnvelopeAsync(response, $"get {resourceType}", cancellationToken);
        }

        /// <summary>Unauthenticated facts about a bridge; the certificate is checked against the root only, since the id is what this call learns.</summary>
        public virtual async Task<HueBridgeInfo?> GetBridgeInfoAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.TryParseHost(bridgeIp, out var host))
            {
                _logger.LogWarning("Bridge address {Address} is not a valid host", bridgeIp);
                return null;
            }

            try
            {
                var uri = BridgeUri.Build(host, "api", "0", "config");
                var response = await SendWithRetryAsync(() => _httpClient.SendAsync(
                    NewRequest(HttpMethod.Get, uri, null, null, null), cancellationToken));
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var excerpt = content.Length > 500 ? content[..500] + "…" : content;

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Bridge returned {Status} for {Operation}: {Content}", response.StatusCode, "bridge config", excerpt);
                    return null;
                }

                using var doc = JsonDocument.Parse(content);
                var bridgeId = GetString(doc.RootElement, "bridgeid")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(bridgeId))
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", "bridge config", excerpt);
                    return null;
                }

                return new HueBridgeInfo(
                    bridgeId,
                    GetString(doc.RootElement, "swversion") ?? string.Empty,
                    GetString(doc.RootElement, "apiversion") ?? string.Empty,
                    GetString(doc.RootElement, "modelid") ?? string.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}", "bridge config");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading bridge configuration from {Host}", host);
                return null;
            }
        }

        /// <summary>Creates an application key. Requires the link button. The raw response is never logged on success.</summary>
        public virtual async Task<AuthenticationOutcome?> AuthenticateAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            var info = await GetBridgeInfoAsync(bridgeIp, cancellationToken);
            if (info == null)
            {
                return null;
            }

            BridgeUri.TryParseHost(bridgeIp, out var host);
            if (!info.SupportsV2)
            {
                _logger.LogWarning("Bridge {Host} is not a supported v2 bridge (software {Version})", host, info.SoftwareVersion);
                return null;
            }

            try
            {
                _logger.LogInformation("Attempting to authenticate with bridge {BridgeId} at {Host}", info.BridgeId, host);

                var uri = BridgeUri.Build(host, "api");
                var body = new { devicetype = DeviceType, generateclientkey = false };
                var response = await SendWithRetryAsync(() => _httpClient.SendAsync(
                    NewRequest(HttpMethod.Post, uri, null, info.BridgeId, body), cancellationToken));
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (content.TrimStart().StartsWith('<'))
                {
                    _logger.LogError("Received HTML response instead of JSON. Bridge may not be accessible at {Host}. Response: {Response}",
                        host, content.Substring(0, Math.Min(200, content.Length)));
                    return null;
                }

                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", "authenticate",
                        content.Length > 500 ? content[..500] + "…" : content);
                    return null;
                }

                var first = doc.RootElement[0];
                var username = GetString(first, "success", "username");
                if (!string.IsNullOrEmpty(username))
                {
                    _bridgeIdsByHost[host] = info.BridgeId;
                    _logger.LogInformation("Successfully authenticated with bridge {BridgeId}", info.BridgeId);
                    return new AuthenticationOutcome(username, info.BridgeId);
                }

                if (first.TryGetProperty("error", out var error))
                {
                    var type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                    _logger.LogWarning("Authentication failed - Type: {Type}, Description: {Error}", type, GetString(error, "description"));
                    // Error bodies carry no key, so they are safe to show.
                    _logger.LogDebug("Authentication response: {Response}", content.Length > 500 ? content[..500] + "…" : content);
                    return null;
                }

                _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", "authenticate",
                    content.Length > 500 ? content[..500] + "…" : content);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}", "authenticate");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication");
                return null;
            }
        }

        /// <summary>Rooms, zones and the bridge home (named "All Lights"), each with its grouped_light service. Null on any failure.</summary>
        public virtual async Task<IReadOnlyList<HueGroupResource>?> GetGroupsAsync(HueBridge bridge, CancellationToken cancellationToken = default)
        {
            try
            {
                var groups = new List<HueGroupResource>();
                foreach (var type in new[] { "room", "zone", "bridge_home" })
                {
                    var data = await GetResourcesAsync(bridge, type, cancellationToken);
                    if (data == null)
                    {
                        return null;
                    }

                    foreach (var item in data.Value.EnumerateArray())
                    {
                        var groupedLightId = FindService(item, "grouped_light");
                        var id = GetString(item, "id");
                        if (groupedLightId == null || id == null)
                        {
                            continue;
                        }

                        var name = type == "bridge_home" ? "All Lights" : (GetString(item, "metadata", "name") ?? type);
                        groups.Add(new HueGroupResource(id, groupedLightId, name, type, GetString(item, "id_v1")));
                    }
                }

                return groups;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups");
                return null;
            }
        }

        public virtual async Task<IReadOnlyList<HueSceneResource>?> GetScenesAsync(HueBridge bridge, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = await GetResourcesAsync(bridge, "scene", cancellationToken);
                if (data == null)
                {
                    return null;
                }

                var scenes = new List<HueSceneResource>();
                foreach (var item in data.Value.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    var groupId = GetString(item, "group", "rid");
                    if (id == null || groupId == null)
                    {
                        continue;
                    }

                    scenes.Add(new HueSceneResource(id, GetString(item, "metadata", "name") ?? id, groupId, GetString(item, "id_v1")));
                }

                return scenes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting scenes");
                return null;
            }
        }

        public virtual async Task<IReadOnlyList<HueLightResource>?> GetLightsAsync(HueBridge bridge, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = await GetResourcesAsync(bridge, "light", cancellationToken);
                if (data == null)
                {
                    return null;
                }

                var lights = new List<HueLightResource>();
                foreach (var item in data.Value.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    if (id == null)
                    {
                        continue;
                    }

                    var on = item.TryGetProperty("on", out var onElement) && onElement.TryGetProperty("on", out var onValue) && onValue.ValueKind == JsonValueKind.True;
                    double? brightness = item.TryGetProperty("dimming", out var dimming) && dimming.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number
                        ? b.GetDouble()
                        : null;
                    lights.Add(new HueLightResource(id, GetString(item, "metadata", "name") ?? id, on, brightness, GetString(item, "id_v1")));
                }

                return lights;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lights");
                return null;
            }
        }

        /// <summary>PUT /clip/v2/resource/grouped_light/{id}. True when the bridge reports no error.</summary>
        public virtual async Task<bool> SetGroupedLightAsync(HueBridge bridge, string groupedLightId, GroupedLightState state, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.IsValidSegment(groupedLightId))
            {
                _logger.LogWarning("Bridge {Host} {Field} '{Value}' is not a valid id", bridge.IpAddress, "grouped light", groupedLightId);
                return false;
            }

            try
            {
                var target = await PrepareAsync(bridge, cancellationToken);
                if (target == null)
                {
                    return false;
                }

                var uri = BridgeUri.Build(target.Value.Host, "clip", "v2", "resource", "grouped_light", groupedLightId);
                var response = await SendWithRetryAsync(() => _httpClient.SendAsync(
                    NewRequest(HttpMethod.Put, uri, bridge.Username, target.Value.BridgeId, state.ToRequestBody()), cancellationToken));
                return await ReadEnvelopeAsync(response, $"grouped_light {groupedLightId}", cancellationToken) != null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting group state");
                return false;
            }
        }

        /// <summary>PUT /clip/v2/resource/scene/{id} with recall.action = active and an optional transition.</summary>
        public virtual async Task<bool> RecallSceneAsync(HueBridge bridge, string sceneId, int? durationMs, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.IsValidSegment(sceneId))
            {
                _logger.LogWarning("Bridge {Host} {Field} '{Value}' is not a valid id", bridge.IpAddress, "scene", sceneId);
                return false;
            }

            try
            {
                var target = await PrepareAsync(bridge, cancellationToken);
                if (target == null)
                {
                    return false;
                }

                var recall = new Dictionary<string, object> { ["action"] = "active" };
                if (durationMs.HasValue)
                {
                    recall["duration"] = durationMs.Value;
                }

                var uri = BridgeUri.Build(target.Value.Host, "clip", "v2", "resource", "scene", sceneId);
                var response = await SendWithRetryAsync(() => _httpClient.SendAsync(
                    NewRequest(HttpMethod.Put, uri, bridge.Username, target.Value.BridgeId, new { recall }), cancellationToken));
                return await ReadEnvelopeAsync(response, $"scene {sceneId}", cancellationToken) != null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating scene {SceneId}", sceneId);
                return false;
            }
        }

        // ---------------------------------------------------------------------------------
        // v1 (deleted in Task 6)
        // ---------------------------------------------------------------------------------

        // The bridge answers state changes with HTTP 200 and a JSON array of
        // {"success":{...}} / {"error":{"type","address","description"}} entries, one per
        // key. An unknown key or a bad API key is therefore invisible to IsSuccessStatusCode.
        private async Task<bool> ReadBridgeResultAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            // A wrong IP can serve a whole web page; keep log lines bounded.
            var excerpt = content.Length > 500 ? content[..500] + "…" : content;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bridge returned {Status} for {Operation}: {Content}", response.StatusCode, operation, excerpt);
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", operation, excerpt);
                    return false;
                }

                var ok = true;
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.TryGetProperty("error", out var error))
                    {
                        ok = false;
                        _logger.LogWarning("Bridge error for {Operation}: type {Type} at {Address}: {Description}",
                            operation,
                            error.TryGetProperty("type", out var t) ? t.ToString() : "?",
                            error.TryGetProperty("address", out var a) ? a.ToString() : "?",
                            error.TryGetProperty("description", out var d) ? d.ToString() : "?");
                    }
                }

                return ok;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}: {Content}", operation, excerpt);
                return false;
            }
        }

        // Retry once on transient connection failures (stale pooled connections to Hue bridge)
        private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> request)
        {
            try
            {
                return await request();
            }
            catch (HttpRequestException ex) when (ex.InnerException is IOException)
            {
                _logger.LogDebug("Connection reset, retrying request");
                return await request();
            }
        }

        // Discover Hue bridges on the network using N-UPnP
        public async Task<List<HueBridgeDiscovery>> DiscoverBridgesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Discovering Hue bridges...");
                
                // Use Philips Hue discovery endpoint
                var response = await _httpClient.GetAsync("https://discovery.meethue.com/", cancellationToken);
                response.EnsureSuccessStatusCode();
                
                var bridges = await response.Content.ReadFromJsonAsync<List<HueBridgeDiscovery>>(cancellationToken);
                
                if (bridges != null)
                {
                    // Normalize all bridge IPs
                    foreach (var bridge in bridges)
                    {
                        bridge.InternalIpAddress = NormalizeBridgeIp(bridge.InternalIpAddress);
                        _logger.LogInformation("Found bridge {Id} at {Ip}", bridge.Id, bridge.InternalIpAddress);
                    }
                }
                
                _logger.LogInformation("Found {Count} Hue bridge(s)", bridges?.Count ?? 0);
                return bridges ?? new List<HueBridgeDiscovery>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering Hue bridges");
                return new List<HueBridgeDiscovery>();
            }
        }

        // Alternative: Local network discovery using SSDP
        public async Task<List<HueBridgeDiscovery>> DiscoverBridgesLocalAsync(CancellationToken cancellationToken = default)
        {
            var bridges = new List<HueBridgeDiscovery>();
            
            try
            {
                _logger.LogInformation("Performing local SSDP discovery...");
                
                using var udpClient = new UdpClient();
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                
                var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                
                var searchMessage = 
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 3\r\n" +
                    "ST: ssdp:all\r\n\r\n";
                
                var searchBytes = Encoding.ASCII.GetBytes(searchMessage);
                await udpClient.SendAsync(searchBytes, searchBytes.Length, multicastEndpoint);
                
                udpClient.Client.ReceiveTimeout = 3000;

                // Enforce a 5-second timeout so the loop doesn't hang indefinitely
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(timeoutCts.Token);
                        var response = Encoding.ASCII.GetString(result.Buffer);

                        if (response.Contains("IpBridge", StringComparison.OrdinalIgnoreCase))
                        {
                            var ip = result.RemoteEndPoint.Address.ToString();
                            var bridgeId = ExtractBridgeIdFromSsdp(response);

                            if (!bridges.Any(b => b.InternalIpAddress == ip))
                            {
                                bridges.Add(new HueBridgeDiscovery
                                {
                                    Id = bridgeId,
                                    InternalIpAddress = ip
                                });
                                _logger.LogInformation("Found Hue bridge at {IpAddress}", ip);
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        break; // Socket timeout
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Discovery timeout elapsed
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during local bridge discovery");
            }
            
            return bridges;
        }

        private string ExtractBridgeIdFromSsdp(string response)
        {
            var lines = response.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("hue-bridgeid:", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Split(':')[1].Trim();
                }
            }
            return Guid.NewGuid().ToString("N")[..16];
        }

        // Normalize bridge IP to remove any protocol and ensure it's just IP:port
        internal static string NormalizeBridgeIp(string bridgeIp) => BridgeUri.StripSchemeAndPath(bridgeIp);

        // Test connection to bridge - returns the raw response for diagnostics
        public virtual async Task<string> TestBridgeConnectionAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            var info = await GetBridgeInfoAsync(bridgeIp, cancellationToken);
            if (info == null)
            {
                return "Error: the bridge did not answer /api/0/config with a bridge id. See the Jellyfin log for the reason.";
            }

            return $"Bridge {info.BridgeId} (model {info.ModelId}, software {info.SoftwareVersion}, API {info.ApiVersion}) — "
                + (info.SupportsV2 ? "supports API v2." : $"does NOT support API v2; bridge software {MinimumV2SoftwareVersion} or newer is required.");
        }

        // Get all lights
        public async Task<Dictionary<string, HueLight>?> GetLightsAsync(string bridgeIp, string username, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var response = await SendWithRetryAsync(() => _httpClient.GetAsync($"https://{bridgeIp}/api/{username}/lights", cancellationToken));
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadFromJsonAsync<Dictionary<string, HueLight>>(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lights");
                return null;
            }
        }

        // Get all groups
        public async Task<Dictionary<string, HueGroup>?> GetGroupsAsync(string bridgeIp, string username, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var response = await SendWithRetryAsync(() => _httpClient.GetAsync($"https://{bridgeIp}/api/{username}/groups", cancellationToken));
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadFromJsonAsync<Dictionary<string, HueGroup>>(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups");
                return null;
            }
        }

        // Get all scenes
        public async Task<Dictionary<string, HueScene>?> GetScenesAsync(string bridgeIp, string username, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var response = await SendWithRetryAsync(() => _httpClient.GetAsync($"https://{bridgeIp}/api/{username}/scenes", cancellationToken));
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadFromJsonAsync<Dictionary<string, HueScene>>(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting scenes");
                return null;
            }
        }

        // Activate a scene
        public virtual async Task<bool> ActivateSceneAsync(string bridgeIp, string username, string groupId, string sceneId, int? transitionTime = null, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                object requestBody = transitionTime.HasValue
                    ? new { scene = sceneId, transitiontime = transitionTime.Value }
                    : new { scene = sceneId };
                var response = await SendWithRetryAsync(() => _httpClient.PutAsJsonAsync(
                    $"https://{bridgeIp}/api/{username}/groups/{groupId}/action",
                    requestBody,
                    HueJsonOptions,
                    cancellationToken));

                return await ReadBridgeResultAsync(response, $"scene {sceneId} on group {groupId}", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating scene {SceneId}", sceneId);
                return false;
            }
        }

        // Set group state (brightness, on/off, etc.)
        public virtual async Task<bool> SetGroupStateAsync(string bridgeIp, string username, string groupId, HueLightState state, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var response = await SendWithRetryAsync(() => _httpClient.PutAsJsonAsync(
                    $"https://{bridgeIp}/api/{username}/groups/{groupId}/action",
                    state,
                    HueJsonOptions,
                    cancellationToken));

                return await ReadBridgeResultAsync(response, $"group {groupId} state", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting group state");
                return false;
            }
        }

        // Set individual light state
        public async Task<bool> SetLightStateAsync(string bridgeIp, string username, string lightId, HueLightState state, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var response = await SendWithRetryAsync(() => _httpClient.PutAsJsonAsync(
                    $"https://{bridgeIp}/api/{username}/lights/{lightId}/state",
                    state,
                    HueJsonOptions,
                    cancellationToken));

                return await ReadBridgeResultAsync(response, $"light {lightId} state", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting light state");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

}
