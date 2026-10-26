using System;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Outer resource safety also joins tasks when a deliberately mutated ownership helper fails.</summary>
internal static class CatalogLifetimeTestSafety
{
    public static async Task JoinAsync(UpgradePublishScenario scenario, Task? cleanup, Exception? primary)
    {
        var safety = new InvalidOperationException("negative proof outer safety");
        scenario.Sequence.Fail(primary ?? safety);
        cleanup ??= scenario.Owner.FinishAsync(primary ?? safety);
        try
        {
            await cleanup;
        }
        catch (Exception)
        {
            // The proof asserts the cleanup outcome. This final safety must still join the other tasks.
        }
        finally
        {
            foreach (Task run in scenario.Owner.Runs)
            {
                try
                {
                    await run;
                }
                catch (Exception)
                {
                    // All owned outcomes were observed by the proof or by its cleanup task above.
                }
            }
        }
    }
}
