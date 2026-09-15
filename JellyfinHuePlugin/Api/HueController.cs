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
        private readonly IHueConfiguration _configuration;
        private readonly LightCommandExecutor _executor;

        public HueController(
            ILogger<HueController> logger,
            HueService hueService,
            HueResourceCatalog catalog,
            IHueConfiguration configuration,
            LightCommandExecutor executor)
        {
            _logger = logger;
            _hueService = hueService;
            _catalog = catalog;
            _configuration = configuration;
            _executor = executor;
        }

        [HttpGet("discover")]
        public async Task<ActionResult<List<HueBridgeDiscovery>>> DiscoverBridges(CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Discovering Hue bridges");

            var bridges = await _hueService.DiscoverBridgesAsync(cancellationToken);

            return Ok(bridges);
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
                var config = _configuration.Current;

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
                else if (!string.Equals(bridge.IpAddress, request.BridgeIp, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(bridge.Username, username, StringComparison.Ordinal))
                {
                    // This entry now describes a different bridge, or a different application
                    // key on it. HueBridge.HardwareId is the 16-hex id the TLS certificate is
                    // pinned to (not the configuration GUID in HueBridge.Id); the assignment
                    // below re-learns it from this authentication, so the old pin never
                    // survives. The cached bridge home describes the old bridge just as much,
                    // so drop it here - the catalog keys by id + address + key, which the
                    // assignments below change.
                    _logger.LogInformation("Bridge {BridgeName} changed address or application key; dropping its cached resources and re-learning its bridge id", bridge.Name);
                    _catalog.Invalidate(bridge);
                }

                bridge.Username = username;
                bridge.HardwareId = outcome.HardwareId;
                bridge.IpAddress = request.BridgeIp;
                _configuration.Save();

                return Ok(new AuthenticationResult { Success = true, Username = username, Id = bridge.Id, HardwareId = outcome.HardwareId });
            }

            return Ok(new AuthenticationResult
            {
                Success = false,
                Error = "Authentication failed. Press the link button on the bridge and try again; the Jellyfin log names the reason (link button, unsupported bridge software, or certificate)."
            });
        }

        // Rooms and zones are keyed by grouped_light id, which is what a profile stores as
        // TargetGroupId, and scenes by scene id. The bridge home isn't listed: the page adds its
        // own "All Lights" option with value "0".
        [HttpGet("targets")]
        public async Task<ActionResult<TargetsResult>> GetTargets(
            [FromQuery] string? bridgeId,
            CancellationToken cancellationToken)
        {
            var bridge = string.IsNullOrWhiteSpace(bridgeId)
                ? null
                : _configuration.Current.Bridges.FirstOrDefault(b => b.Id == bridgeId);
            if (bridge == null || string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                return BadRequest("Bridge not configured");
            }

            _logger.LogInformation("API: Getting rooms, zones and scenes from bridge {BridgeName}", bridge.Name);

            var targets = await _catalog.ReadTargetsAsync(bridge, cancellationToken);
            if (targets == null)
            {
                return StatusCode(500, "Failed to retrieve rooms, zones and scenes");
            }

            return Ok(new TargetsResult
            {
                Groups = targets.Groups.ToDictionary(g => g.GroupedLightId),
                Scenes = targets.Scenes.ToDictionary(s => s.Id)
            });
        }

        [HttpPost("test")]
        public async Task<ActionResult<TestLightResult>> TestLightControl(
            [FromBody][Required] TestLightRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Action is not { } action || !Enum.IsDefined(action) || request.Profile is not { } profile)
            {
                return BadRequest("Action (Play, Pause or Stop) and Profile are required");
            }

            var bridge = _configuration.Current.FindProfileBridge(profile);
            if (bridge == null)
            {
                return Ok(new TestLightResult { Error = TestError(LightCommandOutcome.BridgeNotConfigured) });
            }

            _logger.LogInformation("API: Testing {Action} for profile {ProfileName} on bridge {BridgeName}", action, profile.Name, bridge.Name);

            var outcome = await _executor.ExecuteAsync(action, bridge, profile, cancellationToken);
            return Ok(new TestLightResult { Success = outcome == LightCommandOutcome.Succeeded, Error = TestError(outcome) });
        }

        /// <summary>The reason the page shows under the Test button; null on success.</summary>
        private static string? TestError(LightCommandOutcome outcome) => outcome switch
        {
            LightCommandOutcome.Succeeded => null,
            LightCommandOutcome.BridgeNotConfigured => "Bridge not configured",
            LightCommandOutcome.SceneUnresolved => "Scene not found on the bridge, or the bridge could not be reached; re-select it or check the server logs",
            LightCommandOutcome.GroupUnresolved => "Target group not found on the bridge, or the bridge could not be reached; re-select it or check the server logs",
            _ => "The bridge did not accept the command; check the server logs"
        };

        [HttpPost("verifyconnection")]
        public async Task<ActionResult<VerifyConnectionResult>> VerifyConnection(
            [FromBody][Required] VerifyConnectionRequest request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("API: Verifying authenticated connection to bridge at {BridgeIp}", request.BridgeIp);

            try
            {
                var info = await _hueService.GetBridgeInfoAsync(request.BridgeIp, cancellationToken);
                if (info == null)
                {
                    return Ok(new VerifyConnectionResult { Success = false, Error = "The bridge did not answer /api/0/config. Check the address; the Jellyfin log names the reason." });
                }

                var result = new VerifyConnectionResult
                {
                    HardwareId = info.HardwareId,
                    ModelId = info.ModelId,
                    SoftwareVersion = info.SoftwareVersion,
                    ApiVersion = info.ApiVersion,
                    SupportsV2 = info.SupportsV2
                };

                if (!info.SupportsV2)
                {
                    result.Error = $"Bridge software {info.SoftwareVersion} does not support API v2; {HueService.MinimumV2SoftwareVersion} or newer is required.";
                    return Ok(result);
                }

                // Pin the probe to the id just learned, exactly as a configured bridge would be.
                var probe = new HueBridge { Name = "verify", IpAddress = request.BridgeIp, Username = request.Username, HardwareId = info.HardwareId };
                var lights = await _hueService.GetLightsAsync(probe, cancellationToken);
                if (lights != null)
                {
                    result.Success = true;
                    return Ok(result);
                }

                result.Error = "Bridge returned no data. The API key may be invalid, or the bridge certificate was rejected; the Jellyfin log names the reason.";
                return Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bridge verification failed for {BridgeIp}", request.BridgeIp);
                return Ok(new VerifyConnectionResult { Success = false, Error = "Connection failed: " + ex.Message });
            }
        }
    }

}
