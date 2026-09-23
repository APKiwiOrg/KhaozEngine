using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests;

/// <summary>
/// Arms <see cref="ThreadPoolStarvationWatchdog"/> for this test host when <c>KE_POOL_WATCH=1</c> (#720, #553).
/// A module initializer runs before the first test, so an episode early in the run is still seen. With the
/// variable unset this is one environment read.
/// </summary>
internal static class PoolWatchHook
{
    [ModuleInitializer]
    internal static void Arm() =>
        ThreadPoolStarvationWatchdog.StartIfEnabled(typeof(PoolWatchHook).Assembly.GetName().Name ?? "tests");
}
