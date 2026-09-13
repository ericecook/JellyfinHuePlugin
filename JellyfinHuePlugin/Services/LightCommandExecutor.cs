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
    /// The one place that turns a light action into HueService calls. Owns the transition
    /// logic and the turn-off wait, which is cancellable.
    /// </summary>
    public class LightCommandExecutor
    {
        private readonly HueService _hueService;
        private readonly ILogger _logger;
        private readonly TimeProvider _clock;

        public LightCommandExecutor(HueService hueService, ILogger logger, TimeProvider? timeProvider = null)
        {
            _hueService = hueService;
            _logger = logger;
            _clock = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Sends the profile's commands for the action to the bridge. Warns and returns when
        /// the bridge has no IP or username. Logs and swallows HueService failures. Lets
        /// OperationCanceledException propagate when the token is cancelled.
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

        private async Task PlayAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            int? transitionTime = profile.EnablePlayTransition ? profile.PlayTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.PlaySceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating play scene {SceneId}", profile.Name, profile.PlaySceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.PlaySceneId,
                    transitionTime,
                    cancellationToken);
            }
            else if (profile.TurnOffLightsOnPlay)
            {
                _logger.LogInformation("[{ProfileName}] Turning off lights in group {GroupId}", profile.Name, profile.TargetGroupId);
                if (transitionTime.HasValue && transitionTime.Value > 0)
                {
                    // Dim to minimum first so the transition is visible, then turn off
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = true, Bri = 1, TransitionTime = transitionTime },
                        cancellationToken);
                    // Wait for the transition to complete (transitionTime is in deciseconds; ×100 = milliseconds).
                    // A newer command for the session cancels this wait.
                    await Task.Delay(TimeSpan.FromMilliseconds(transitionTime.Value * 100), _clock, cancellationToken);
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = false },
                        cancellationToken);
                }
                else
                {
                    await _hueService.SetGroupStateAsync(
                        bridge.IpAddress,
                        bridge.Username,
                        profile.TargetGroupId,
                        new HueLightState { On = false },
                        cancellationToken);
                }
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Dimming lights to {Brightness}", profile.Name, profile.PlayBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PlayBrightness, TransitionTime = transitionTime },
                    cancellationToken);
            }
        }

        private async Task PauseAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            int? transitionTime = profile.EnablePauseTransition ? profile.PauseTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.PauseSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating pause scene {SceneId}", profile.Name, profile.PauseSceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.PauseSceneId,
                    transitionTime,
                    cancellationToken);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Brightening lights to {Brightness}", profile.Name, profile.PauseBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.PauseBrightness, TransitionTime = transitionTime },
                    cancellationToken);
            }
        }

        private async Task StopAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            int? transitionTime = profile.EnableStopTransition ? profile.StopTransitionDuration : null;

            if (!string.IsNullOrWhiteSpace(profile.StopSceneId))
            {
                _logger.LogInformation("[{ProfileName}] Activating stop scene {SceneId}", profile.Name, profile.StopSceneId);
                await _hueService.ActivateSceneAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    profile.StopSceneId,
                    transitionTime,
                    cancellationToken);
            }
            else
            {
                _logger.LogInformation("[{ProfileName}] Turning lights on to {Brightness}", profile.Name, profile.StopBrightness);
                await _hueService.SetGroupStateAsync(
                    bridge.IpAddress,
                    bridge.Username,
                    profile.TargetGroupId,
                    new HueLightState { On = true, Bri = profile.StopBrightness, TransitionTime = transitionTime },
                    cancellationToken);
            }
        }
    }
}
