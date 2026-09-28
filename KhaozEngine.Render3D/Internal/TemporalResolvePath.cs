using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Which resolve the temporal resolve renderer records: the single fullscreen pass
    /// (<see cref="ShaderSources.TemporalResolveFrag"/>), the default, or the two-pass resolve
    /// (<see cref="ShaderSources.TemporalPrepareFrag"/> then <see cref="ShaderSources.TemporalSplitFrag"/>), a
    /// measured alternative. The environment variable <c>KE_TEMPORAL_RESOLVE</c> set to <c>split</c> selects the
    /// two-pass resolve for every renderer the process creates. It is read once. A scene or a renderer can still be
    /// switched on its own, which is how one test times both.
    /// </summary>
    internal static class TemporalResolvePath
    {
        /// <summary>The environment variable that selects the resolve for the process.</summary>
        public const string EnvironmentVariable = "KE_TEMPORAL_RESOLVE";

        /// <summary>Whether <see cref="EnvironmentVariable"/> selected the two-pass resolve when the process first
        /// asked.</summary>
        public static bool SplitFromEnvironment { get; } = string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable), "split", StringComparison.OrdinalIgnoreCase);
    }
}
