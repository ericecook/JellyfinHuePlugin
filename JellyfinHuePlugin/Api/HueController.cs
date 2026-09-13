using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Api
{
    [ApiController]
    // Plain [Authorize] admits any signed-in Jellyfin account. Bridge keys, light
    // control and outbound test connections are admin-only, like the plugin config page.
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("api/hueplugin")]
    [Produces(MediaTypeNames.Application.Json)]
    public class HueController : ControllerBase
    {
        private readonly ILogger<HueController> _logger;
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;

        public HueController(ILogger<HueController> logger)
        {
            _logger = logger;

            if (Plugin.Instance == null)
            {
                throw new InvalidOperationException("Plugin instance not initialized");
            }

            _hueService = Plugin.Instance.HueService;
            _catalog = Plugin.Instance.Catalog;
        }

        private HueBridge? GetBridge(string? bridgeId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return null;

            if (!string.IsNullOrWhiteSpace(bridgeId))
            {
                return config.Bridges.FirstOrDefault(b => b.Id == bridgeId);
            }

            // Fallback to first bridge
            return config.Bridges.FirstOrDefault();
        }

        [HttpGet("discover")]
        public async Task<ActionResult<List<HueBridgeDiscovery>>> DiscoverBridges(CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Discovering Hue bridges");

            var bridges = await _hueService.DiscoverBridgesAsync(cancellationToken);

            return Ok(bridges);
        }

        [HttpGet("bridges")]
        public ActionResult<List<BridgeInfo>> GetBridges()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode(500, "Plugin not initialized");
            }

            var bridges = config.Bridges.Select(b => new BridgeInfo
            {
                Id = b.Id,
                Name = b.Name,
                IpAddress = b.IpAddress,
                IsAuthenticated = !string.IsNullOrWhiteSpace(b.Username)
            }).ToList();

            return Ok(bridges);
        }

        [HttpPost("bridges")]
        public ActionResult<BridgeInfo> AddBridge([FromBody][Required] AddBridgeRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode(500, "Plugin not initialized");
            }

            var bridge = new HueBridge
            {
                IpAddress = request.IpAddress,
                Name = request.Name
            };
            config.Bridges.Add(bridge);
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("Added bridge {BridgeName} at {IpAddress}", bridge.Name, bridge.IpAddress);

            return Ok(new BridgeInfo
            {
                Id = bridge.Id,
                Name = bridge.Name,
                IpAddress = bridge.IpAddress,
                IsAuthenticated = false
            });
        }

        [HttpDelete("bridges/{bridgeId}")]
        public ActionResult DeleteBridge(string bridgeId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode(500, "Plugin not initialized");
            }

            var bridge = config.Bridges.FirstOrDefault(b => b.Id == bridgeId);
            if (bridge == null)
            {
                return NotFound("Bridge not found");
            }

            config.Bridges.Remove(bridge);
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("Deleted bridge {BridgeName} ({BridgeId})", bridge.Name, bridgeId);

            return Ok();
        }

        [HttpPost("authenticate")]
        public async Task<ActionResult<AuthenticationResult>> Authenticate(
            [FromBody][Required] AuthenticationRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Attempting authentication with bridge {BridgeIp}", request.BridgeIp);

            var outcome = await _hueService.AuthenticateAsync(request.BridgeIp, cancellationToken);

            if (outcome != null)
            {
                var username = outcome.Username;
                var config = Plugin.Instance?.Configuration;
                if (config != null)
                {
                    HueBridge? bridge = null;

                    // Find existing bridge by ID or IP
                    if (!string.IsNullOrWhiteSpace(request.BridgeId))
                    {
                        bridge = config.Bridges.FirstOrDefault(b => b.Id == request.BridgeId);
                    }

                    bridge ??= config.Bridges.FirstOrDefault(b => b.IpAddress == request.BridgeIp);

                    if (bridge == null)
                    {
                        // Create new bridge entry
                        bridge = new HueBridge
                        {
                            IpAddress = request.BridgeIp,
                            Name = request.BridgeName ?? "Bridge"
                        };
                        config.Bridges.Add(bridge);
                    }

                    bridge.Username = username;
                    bridge.BridgeId = outcome.BridgeId;
                    bridge.IpAddress = request.BridgeIp;
                    Plugin.Instance?.SaveConfiguration();

                    return Ok(new AuthenticationResult { Success = true, Username = username, BridgeId = bridge.Id });
                }

                return Ok(new AuthenticationResult { Success = true, Username = username });
            }

            return Ok(new AuthenticationResult
            {
                Success = false,
                Error = "Authentication failed. Press the link button on the bridge and try again; the Jellyfin log names the reason (link button, unsupported bridge software, or certificate)."
            });
        }

        [HttpGet("lights")]
        public async Task<ActionResult<Dictionary<string, HueLightResource>>> GetLights(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting lights from bridge {BridgeName}", bridge.Name);

            var lights = await _hueService.GetLightsAsync(bridge, cancellationToken);
            if (lights == null)
            {
                return StatusCode(500, "Failed to retrieve lights");
            }

            return Ok(lights.ToDictionary(l => l.Id));
        }

        // Keyed by grouped_light id, which is what a profile stores as TargetGroupId. The bridge
        // home is left out: the page adds its own "All Lights" option with value "0".
        [HttpGet("groups")]
        public async Task<ActionResult<Dictionary<string, HueGroupResource>>> GetGroups(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting groups from bridge {BridgeName}", bridge.Name);

            var groups = await _catalog.GetGroupsAsync(bridge, cancellationToken);
            if (groups == null)
            {
                return StatusCode(500, "Failed to retrieve groups");
            }

            return Ok(groups.Where(g => g.Type != "bridge_home").ToDictionary(g => g.GroupedLightId));
        }

        [HttpGet("scenes")]
        public async Task<ActionResult<Dictionary<string, HueSceneResource>>> GetScenes(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting scenes from bridge {BridgeName}", bridge.Name);

            var scenes = await _catalog.GetScenesAsync(bridge, cancellationToken);
            if (scenes == null)
            {
                return StatusCode(500, "Failed to retrieve scenes");
            }

            return Ok(scenes.ToDictionary(s => s.Id));
        }

        [HttpPost("test")]
        public async Task<ActionResult> TestLightControl(
            [FromBody][Required] TestLightRequest request,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(request.BridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Testing light control on bridge {BridgeName}", bridge.Name);

            bool success;
            if (!string.IsNullOrWhiteSpace(request.SceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, request.SceneId, cancellationToken);
                if (sceneId == null)
                {
                    return StatusCode(500, "Scene not found on the bridge; re-select it");
                }

                success = await _hueService.RecallSceneAsync(bridge, sceneId, 1000, cancellationToken);
            }
            else
            {
                var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, request.GroupId ?? "0", cancellationToken);
                if (groupedLightId == null)
                {
                    return StatusCode(500, "Target group not found on the bridge; re-select it");
                }

                success = await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                    new GroupedLightState { On = true, Brightness = Math.Clamp(request.Brightness, 0, 100), DurationMs = 1000 },
                    cancellationToken);
            }

            if (success)
            {
                return Ok(new { message = "Light control test successful" });
            }

            return StatusCode(500, "Failed to control lights");
        }

        [HttpPost("testconnection")]
        public async Task<ActionResult<string>> TestBridgeConnection(
            [FromBody][Required] TestConnectionRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Testing bridge connection to {BridgeIp}", request.BridgeIp);

            var result = await _hueService.TestBridgeConnectionAsync(request.BridgeIp, cancellationToken);

            return Ok(new { result });
        }

        [HttpPost("verifyconnection")]
        public async Task<ActionResult<VerifyConnectionResult>> VerifyConnection(
            [FromBody][Required] VerifyConnectionRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Verifying authenticated connection to bridge at {BridgeIp}", request.BridgeIp);

            try
            {
                var probe = new HueBridge { Name = "verify", IpAddress = request.BridgeIp, Username = request.Username };
                var lights = await _hueService.GetLightsAsync(probe, cancellationToken);
                if (lights != null)
                {
                    return Ok(new VerifyConnectionResult { Success = true });
                }

                return Ok(new VerifyConnectionResult { Success = false, Error = "Bridge returned no data. The API key may be invalid." });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bridge verification failed for {BridgeIp}", request.BridgeIp);
                return Ok(new VerifyConnectionResult { Success = false, Error = "Connection failed: " + ex.Message });
            }
        }
    }

}
