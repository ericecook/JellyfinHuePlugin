using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

        [HttpPost("authenticate")]
        public async Task<ActionResult<AuthenticationResult>> Authenticate(
            [FromBody][Required] AuthenticationRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Attempting authentication with bridge {BridgeIp}", request.BridgeIp);

            var username = await _hueService.AuthenticateAsync(request.BridgeIp, cancellationToken: cancellationToken);

            if (username != null)
            {
                // Success! Save to configuration
                var config = Plugin.Instance?.Configuration;
                if (config != null)
                {
                    config.BridgeIpAddress = request.BridgeIp;
                    config.Username = username;
                    Plugin.Instance?.SaveConfiguration();
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
        public async Task<ActionResult<Dictionary<string, HueLight>>> GetLights(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.BridgeIpAddress) || string.IsNullOrWhiteSpace(config.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting lights from bridge");
            
            var lights = await _hueService.GetLightsAsync(config.BridgeIpAddress, config.Username, cancellationToken);
            
            if (lights == null)
            {
                return StatusCode(500, "Failed to retrieve lights");
            }
            
            return Ok(lights);
        }

        [HttpGet("groups")]
        public async Task<ActionResult<Dictionary<string, HueGroup>>> GetGroups(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.BridgeIpAddress) || string.IsNullOrWhiteSpace(config.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting groups from bridge");
            
            var groups = await _hueService.GetGroupsAsync(config.BridgeIpAddress, config.Username, cancellationToken);
            
            if (groups == null)
            {
                return StatusCode(500, "Failed to retrieve groups");
            }
            
            return Ok(groups);
        }

        [HttpGet("scenes")]
        public async Task<ActionResult<Dictionary<string, HueScene>>> GetScenes(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.BridgeIpAddress) || string.IsNullOrWhiteSpace(config.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting scenes from bridge");
            
            var scenes = await _hueService.GetScenesAsync(config.BridgeIpAddress, config.Username, cancellationToken);
            
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
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.BridgeIpAddress) || string.IsNullOrWhiteSpace(config.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Testing light control");

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
                    config.BridgeIpAddress,
                    config.Username,
                    request.GroupId ?? "0",
                    request.SceneId,
                    cancellationToken: cancellationToken);
            }
            else
            {
                success = await _hueService.SetGroupStateAsync(
                    config.BridgeIpAddress,
                    config.Username,
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
    }

}
