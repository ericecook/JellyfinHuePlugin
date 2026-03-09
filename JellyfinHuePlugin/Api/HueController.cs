using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;

namespace JellyfinHuePlugin.Api
{
    [ApiController]
    [Authorize]
    [Route("api/hueplugin")]
    [Produces(MediaTypeNames.Application.Json)]
    public class HueController : ControllerBase
    {
        private readonly ILogger<HueController> _logger;
        private readonly HueService _hueService;

        public HueController(ILogger<HueController> logger)
        {
            _logger = logger;

            if (Plugin.Instance == null)
            {
                throw new InvalidOperationException("Plugin instance not initialized");
            }

            _hueService = Plugin.Instance.HueService;
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

            // If no bridges found via cloud, try local discovery
            if (bridges.Count == 0)
            {
                _logger.LogInformation("No bridges found via cloud, trying local discovery");
                bridges = await _hueService.DiscoverBridgesLocalAsync(cancellationToken);
            }

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

            var username = await _hueService.AuthenticateAsync(request.BridgeIp, cancellationToken: cancellationToken);

            if (username != null)
            {
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
                    bridge.IpAddress = request.BridgeIp;
                    Plugin.Instance?.SaveConfiguration();

                    return Ok(new AuthenticationResult { Success = true, Username = username, BridgeId = bridge.Id });
                }

                return Ok(new AuthenticationResult { Success = true, Username = username });
            }

            return Ok(new AuthenticationResult
            {
                Success = false,
                Error = "Link button not pressed. Press the button on your Hue bridge and try again."
            });
        }

        [HttpGet("lights")]
        public async Task<ActionResult<Dictionary<string, HueLight>>> GetLights(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting lights from bridge {BridgeName}", bridge.Name);

            var lights = await _hueService.GetLightsAsync(bridge.IpAddress, bridge.Username, cancellationToken);

            if (lights == null)
            {
                return StatusCode(500, "Failed to retrieve lights");
            }

            return Ok(lights);
        }

        [HttpGet("groups")]
        public async Task<ActionResult<Dictionary<string, HueGroup>>> GetGroups(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting groups from bridge {BridgeName}", bridge.Name);

            var groups = await _hueService.GetGroupsAsync(bridge.IpAddress, bridge.Username, cancellationToken);

            if (groups == null)
            {
                return StatusCode(500, "Failed to retrieve groups");
            }

            return Ok(groups);
        }

        [HttpGet("scenes")]
        public async Task<ActionResult<Dictionary<string, HueScene>>> GetScenes(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = GetBridge(bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting scenes from bridge {BridgeName}", bridge.Name);

            var scenes = await _hueService.GetScenesAsync(bridge.IpAddress, bridge.Username, cancellationToken);

            if (scenes == null)
            {
                return StatusCode(500, "Failed to retrieve scenes");
            }

            return Ok(scenes);
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

            var state = new HueLightState
            {
                On = true,
                Bri = request.Brightness,
                TransitionTime = 10
            };

            bool success;
            if (!string.IsNullOrWhiteSpace(request.SceneId))
            {
                success = await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    request.GroupId ?? "0",
                    request.SceneId,
                    cancellationToken: cancellationToken);
            }
            else
            {
                success = await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    request.GroupId ?? "0",
                    state,
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
                var lights = await _hueService.GetLightsAsync(request.BridgeIp, request.Username, cancellationToken);
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
