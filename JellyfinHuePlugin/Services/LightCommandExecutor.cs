using System;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    /// <summary>A light action. The plugin page sends it by name ("Pause") whatever the host's JSON options are.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LightAction>))]
    public enum LightAction
    {
        Play,
        Pause,
        Stop
    }

    /// <summary>
    /// What <see cref="LightCommandExecutor.ExecuteAsync"/> did. The two unresolved outcomes mean
    /// the catalog returned no id: the target is missing, the bridge could not be asked, or a
    /// resolve lost a race with an invalidate. The catalog's own log line says which.
    /// </summary>
    public enum LightCommandOutcome
    {
        Succeeded,
        BridgeNotConfigured,
        SceneUnresolved,
        GroupUnresolved,
        Failed
    }

    /// <summary>
    /// The one place that turns a light action into HueService calls, for playback and for the
    /// plugin page's Test buttons. Resolves the profile's target group and scenes through the
    /// catalog, sends brightness as percent and transitions in milliseconds. Turn-off with a
    /// transition is a single fade-out request.
    /// </summary>
    public class LightCommandExecutor
    {
        private readonly HueService _hueService;
        private readonly HueResourceCatalog _catalog;
        private readonly ILogger _logger;

        public LightCommandExecutor(HueService hueService, HueResourceCatalog catalog, ILogger<LightCommandExecutor> logger)
        {
            _hueService = hueService;
            _catalog = catalog;
            _logger = logger;
        }

        /// <summary>
        /// Sends the profile's commands for the action to the bridge and reports the outcome.
        /// Warns and returns <see cref="LightCommandOutcome.BridgeNotConfigured"/> when the bridge
        /// has no IP or username; returns an unresolved outcome when the catalog cannot resolve the
        /// target (the catalog warns); returns <see cref="LightCommandOutcome.Failed"/> when
        /// HueService reports failure or throws (logged). Lets OperationCanceledException
        /// propagate when the token is cancelled.
        /// </summary>
        public virtual async Task<LightCommandOutcome> ExecuteAsync(LightAction action, HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(bridge.IpAddress) || string.IsNullOrWhiteSpace(bridge.Username))
            {
                _logger.LogWarning("Bridge {BridgeName} not fully configured", bridge.Name);
                return LightCommandOutcome.BridgeNotConfigured;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var outcome = action switch
                {
                    LightAction.Play => await PlayAsync(bridge, profile, cancellationToken),
                    LightAction.Pause => await PauseAsync(bridge, profile, cancellationToken),
                    LightAction.Stop => await StopAsync(bridge, profile, cancellationToken),
                    _ => LightCommandOutcome.Failed
                };

                _logger.LogDebug("[{ProfileName}] {Action} command finished in {ElapsedMs} ms ({Outcome})",
                    profile.Name, action, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome);
                return outcome;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling Hue lights for state {State}", action);
                return LightCommandOutcome.Failed;
            }
        }

        /// <summary>Deciseconds in the configuration, milliseconds on the wire.</summary>
        private static int? DurationMs(bool enabled, int deciseconds) => enabled ? deciseconds * 100 : null;

        /// <summary>The page clamps on save, but a profile can also arrive through the generic configuration save or the test endpoint.</summary>
        private static int Percent(int brightness) => Math.Clamp(brightness, 0, 100);

        private static LightCommandOutcome Sent(bool accepted) => accepted ? LightCommandOutcome.Succeeded : LightCommandOutcome.Failed;

        private async Task<LightCommandOutcome> PlayAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnablePlayTransition, profile.PlayTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.PlaySceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.PlaySceneId, cancellationToken);
                if (sceneId == null)
                {
                    return LightCommandOutcome.SceneUnresolved;
                }

                _logger.LogInformation("[{ProfileName}] Activating play scene {SceneId}", profile.Name, profile.PlaySceneId);
                return Sent(await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken));
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return LightCommandOutcome.GroupUnresolved;
            }

            if (profile.TurnOffLightsOnPlay)
            {
                _logger.LogInformation("[{ProfileName}] Turning off lights in group {GroupId}", profile.Name, profile.TargetGroupId);
                return Sent(await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                    new GroupedLightState { On = false, DurationMs = durationMs }, cancellationToken));
            }

            var brightness = Percent(profile.PlayBrightness);
            _logger.LogInformation("[{ProfileName}] Dimming lights to {Brightness}", profile.Name, brightness);
            return Sent(await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = brightness, DurationMs = durationMs }, cancellationToken));
        }

        private async Task<LightCommandOutcome> PauseAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnablePauseTransition, profile.PauseTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.PauseSceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.PauseSceneId, cancellationToken);
                if (sceneId == null)
                {
                    return LightCommandOutcome.SceneUnresolved;
                }

                _logger.LogInformation("[{ProfileName}] Activating pause scene {SceneId}", profile.Name, profile.PauseSceneId);
                return Sent(await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken));
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return LightCommandOutcome.GroupUnresolved;
            }

            var brightness = Percent(profile.PauseBrightness);
            _logger.LogInformation("[{ProfileName}] Brightening lights to {Brightness}", profile.Name, brightness);
            return Sent(await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = brightness, DurationMs = durationMs }, cancellationToken));
        }

        private async Task<LightCommandOutcome> StopAsync(HueBridge bridge, LightControlProfile profile, CancellationToken cancellationToken)
        {
            var durationMs = DurationMs(profile.EnableStopTransition, profile.StopTransitionDuration);

            if (!string.IsNullOrWhiteSpace(profile.StopSceneId))
            {
                var sceneId = await _catalog.ResolveSceneAsync(bridge, profile.StopSceneId, cancellationToken);
                if (sceneId == null)
                {
                    return LightCommandOutcome.SceneUnresolved;
                }

                _logger.LogInformation("[{ProfileName}] Activating stop scene {SceneId}", profile.Name, profile.StopSceneId);
                return Sent(await _hueService.RecallSceneAsync(bridge, sceneId, durationMs, cancellationToken));
            }

            var groupedLightId = await _catalog.ResolveGroupedLightAsync(bridge, profile.TargetGroupId, cancellationToken);
            if (groupedLightId == null)
            {
                return LightCommandOutcome.GroupUnresolved;
            }

            var brightness = Percent(profile.StopBrightness);
            _logger.LogInformation("[{ProfileName}] Turning lights on to {Brightness}", profile.Name, brightness);
            return Sent(await _hueService.SetGroupedLightAsync(bridge, groupedLightId,
                new GroupedLightState { On = true, Brightness = brightness, DurationMs = durationMs }, cancellationToken));
        }
    }
}
