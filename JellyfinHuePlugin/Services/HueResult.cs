using System;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// What a bridge call produced, and -- when it produced nothing -- why. <see cref="Reason"/> is
    /// a finished, admin-facing sentence, written by the plugin: never the application key, never a
    /// stack trace, never a raw response body (bridge-supplied text appears only as the quoted Hue
    /// error <c>description</c>). The compiler forces every caller to look at <see cref="Reason"/>
    /// before it can reach <see cref="Value"/>, since a failed call leaves <see cref="Value"/>
    /// <see langword="default"/>.
    /// </summary>
    public readonly record struct HueResult<T>(T? Value, string? Reason) where T : class
    {
        public bool Ok => Reason is null;

        public static HueResult<T> Success(T value) => new(value, null);

        public static HueResult<T> Failure(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return new(default, reason);
        }
    }

    /// <summary>
    /// Whether a bridge write succeeded, and why not when it didn't. <see cref="HueResult{T}"/>
    /// needs a reference-type payload on success (its <c>where T : class</c> constraint), but
    /// SetGroupedLightAsync and RecallSceneAsync have nothing to return beyond pass/fail -- so this
    /// is HueResult's valueless sibling, built the same way, rather than an instance of it.
    /// </summary>
    public readonly record struct HueOutcome(string? Reason)
    {
        public bool Ok => Reason is null;

        public static readonly HueOutcome Success = new(null);

        public static HueOutcome Failure(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return new(reason);
        }
    }
}
