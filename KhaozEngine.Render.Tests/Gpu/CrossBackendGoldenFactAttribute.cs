using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Runs the committed cross-backend agreement guard during ordinary verification and skips it while one
    /// backend family is being baked. A bake deliberately changes one family before the other families exist,
    /// so comparing the committed set during that transition cannot report a useful failure.
    /// </summary>
    internal sealed class CrossBackendGoldenFactAttribute : FactAttribute
    {
        public CrossBackendGoldenFactAttribute()
        {
            Skip = SkipReason(Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS"));
        }

        internal static string? SkipReason(string? updateGoldens)
            => updateGoldens == "1"
                ? "cross-backend agreement is skipped while KE_UPDATE_GOLDENS=1 is baking one backend family"
                : null;
    }
}
