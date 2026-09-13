using System;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    public enum LightAction
    {
        Play,
        Pause,
        Stop
    }

    /// <summary>
    /// The one place that turns a light action into HueService calls. Resolves the profile's
    /// target group and scenes through the catalog, sends brightness as percent and
    /// transitions in milliseconds. Turn-off with a transition is a single fade-out request.
    /// </summary>
    public class LightCommandExecutor
    {
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;
        private readonly ILogger _logger;

        public LightCommandExecutor(HueService hueService, HueResourceCatalog catalog, ILogger logger)
        {
            _hueService = hueService;
            _catalog = catalog;
            _logger = logger;
        }

        /// <summary>
        /// Sends the profile's commands for the action to the bridge. Warns and returns when
        /// the bridge has no IP or username, or when the target cannot be resolved (the catalog
        /// warns). Logs and swallows HueService failures. Lets OperationCanceledException
        /// propagate when the token is cancelled.
        /// </summary>
        public virtual async Task ExecuteAsync(LightAction action, HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                _logger.LogWarning("Bridge {BridgeName} not fully configured", bridge.Name);
                return;
            }

            try
            {
                switch (action)
                {
                    case LightAction.Play:
                        await PlayAsync(bridge, profile, cancellationToken);
                        break;
                    case LightAction.Pause:
                        await PauseAsync(bridge, profile, cancellationToken);
                        break;
                    case LightAction.Stop:
                        await StopAsync(bridge, profile, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling Hue lights for state {State}", action);
            }
        }

        /// <summary>Deciseconds in the configuration, milliseconds on the wire.</summary>
        private static int? DurationMs(bool enabled, int deciseconds) => enabled ? deciseconds * 100 : null;

        private async Task PlayAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnablePlayTransition, profile.PlayTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.PlaySceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.PlaySceneId, cancellationToken);
                if (sceneId == null)
                {
                    return;
                }

                _logger.LogInformation("[{ProfileName}] Activating play scene {SceneId}", profile.Name, profile.PlaySceneId);
                await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken);
                return;
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return;
            }

            if (profile.TurnOffLightsOnPlay)
            {
                _logger.LogInformation("[{ProfileName}] Turning off lights in group {GroupId}", profile.Name, profile.TargetGroupId);
                await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                    new GroupedLightState { On = false, DurationMs = durationMs }, cancellationToken);
                return;
            }

            _logger.LogInformation("[{ProfileName}] Dimming lights to {Brightness}", profile.Name, profile.PlayBrightness);
            await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = profile.PlayBrightness, DurationMs = durationMs }, cancellationToken);
        }

        private async Task PauseAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnablePauseTransition, profile.PauseTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.PauseSceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.PauseSceneId, cancellationToken);
                if (sceneId == null)
                {
                    return;
                }

                _logger.LogInformation("[{ProfileName}] Activating pause scene {SceneId}", profile.Name, profile.PauseSceneId);
                await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken);
                return;
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return;
            }

            _logger.LogInformation("[{ProfileName}] Brightening lights to {Brightness}", profile.Name, profile.PauseBrightness);
            await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = profile.PauseBrightness, DurationMs = durationMs }, cancellationToken);
        }

        private async Task StopAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnableStopTransition, profile.StopTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.StopSceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.StopSceneId, cancellationToken);
                if (sceneId == null)
                {
                    return;
                }

                _logger.LogInformation("[{ProfileName}] Activating stop scene {SceneId}", profile.Name, profile.StopSceneId);
                await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken);
                return;
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return;
            }

            _logger.LogInformation("[{ProfileName}] Turning lights on to {Brightness}", profile.Name, profile.StopBrightness);
            await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = profile.StopBrightness, DurationMs = durationMs }, cancellationToken);
        }
    }
}
