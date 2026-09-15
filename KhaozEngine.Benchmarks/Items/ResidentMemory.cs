using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Two readings of what the process is holding, because the obvious instrument is not accurate enough
/// to judge a budget on. Against a known live set of 27,620,000 bytes built with the churn a page
/// builder produces, <c>GC.GetTotalMemory(true)</c> answered 61,105,168 and
/// <c>GetGCMemoryInfo().HeapSizeBytes</c> answered 27,800,304 on the same run. So the heap size after a
/// forced compacting collection is the HEADLINE and the <c>GetTotalMemory</c> delta is reported beside
/// it rather than instead of it.
/// </summary>
internal static class ResidentMemory
{
    internal static long Read()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo().HeapSizeBytes;
    }

    internal static long ReadTotalMemory() => GC.GetTotalMemory(forceFullCollection: true);
}
