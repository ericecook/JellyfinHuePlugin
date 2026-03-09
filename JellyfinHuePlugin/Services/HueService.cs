using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    public class HueService : IDisposable
    {
        private readonly ILogger<HueService> _logger;
        private readonly HttpClient _httpClient;

        public HueService(ILogger<HueService> logger)
        {
            _logger = logger;
            
            // Create HttpClient with handler that doesn't validate SSL for local bridge
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AllowAutoRedirect = false // Prevent HTTP->HTTPS redirects
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
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

        // Authenticate with the bridge (requires button press)
        public async Task<string?> AuthenticateAsync(string bridgeIp, string deviceType = "jellyfin_hue_plugin", CancellationToken cancellationToken = default)
        {
            try
            {
                // Normalize the bridge IP - remove any protocol prefix
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                
                _logger.LogInformation("Attempting to authenticate with bridge at {BridgeIp}", bridgeIp);
                
                var requestBody = new { devicetype = deviceType };
                
                // Hue Bridge v2 requires HTTPS
                var url = $"https://{bridgeIp}/api";
                
                _logger.LogDebug("Authentication URL: {Url}", url);
                
                var response = await SendWithRetryAsync(() => _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken));

                // Read response as string first to check what we got
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Authentication response: {Response}", responseContent);
                
                // Check if response is HTML (error page)
                if (responseContent.TrimStart().StartsWith("<"))
                {
                    _logger.LogError("Received HTML response instead of JSON. Bridge may not be accessible at {Url}. Response: {Response}", 
                        url, responseContent.Substring(0, Math.Min(200, responseContent.Length)));
                    return null;
                }
                
                // Try to parse as JSON
                var results = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(responseContent);
                
                if (results != null && results.Count > 0)
                {
                    var result = results[0];
                    
                    if (result.ContainsKey("success"))
                    {
                        var username = result["success"].GetProperty("username").GetString();
                        _logger.LogInformation("Successfully authenticated with bridge");
                        return username;
                    }
                    else if (result.ContainsKey("error"))
                    {
                        var errorDesc = result["error"].GetProperty("description").GetString();
                        var errorType = result["error"].GetProperty("type").GetInt32();
                        _logger.LogWarning("Authentication failed - Type: {Type}, Description: {Error}", errorType, errorDesc);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication");
            }
            
            return null;
        }
        
        // Normalize bridge IP to remove any protocol and ensure it's just IP:port
        private string NormalizeBridgeIp(string bridgeIp)
        {
            if (string.IsNullOrWhiteSpace(bridgeIp))
            {
                return bridgeIp;
            }
            
            // Remove http:// or https://
            bridgeIp = bridgeIp.Replace("https://", "").Replace("http://", "");
            
            // Remove trailing slash
            bridgeIp = bridgeIp.TrimEnd('/');
            
            // Remove any path components (e.g., /api)
            var slashIndex = bridgeIp.IndexOf('/');
            if (slashIndex > 0)
            {
                bridgeIp = bridgeIp.Substring(0, slashIndex);
            }
            
            return bridgeIp;
        }
        
        // Test connection to bridge - returns the raw response for diagnostics
        public async Task<string> TestBridgeConnectionAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            try
            {
                bridgeIp = NormalizeBridgeIp(bridgeIp);
                var url = $"https://{bridgeIp}/api/config";
                
                _logger.LogInformation("Testing connection to bridge at {Url}", url);
                
                var response = await SendWithRetryAsync(() => _httpClient.GetAsync(url, cancellationToken));
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogInformation("Bridge test response - Status: {Status}, Content length: {Length}",
                    response.StatusCode, content.Length);
                
                return $"Status: {response.StatusCode}\nContent: {content.Substring(0, Math.Min(500, content.Length))}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing bridge connection");
                return $"Error: {ex.Message}";
            }
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
                    cancellationToken));

                return response.IsSuccessStatusCode;
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
                    cancellationToken));

                return response.IsSuccessStatusCode;
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
                    cancellationToken));

                return response.IsSuccessStatusCode;
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
