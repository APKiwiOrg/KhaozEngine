using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Two readings of what the process is holding, because the obvious instrument is not accurate enough
/// to judge a budget on. Against a known live set of 27,620,000 bytes built with the churn a page
/// builder produces, <c>GC.GetTotalMemory(true)</c> answered 61,105,168 and
/// <c>GetGCMemoryInfo().HeapSizeBytes</c> answered 27,800,304 on the same run. So the heap size after a
/// forced compacting collection is the HEADLINE and the <c>GetTotalMemory</c> delta is reported beside
/// it rather than instead of it.
/// <para>
/// THE KIND ARGUMENT IS LOAD-BEARING. <c>GetGCMemoryInfo()</c> with no argument reports the latest GC
/// OF ANY KIND, which on a loaded machine is frequently a BACKGROUND collection that finished after the
/// forced one rather than the forced one itself. Two such readings then describe two different
/// collections, and subtracting them measures GC weather instead of retention: a full-solution suite
/// run produced a page delta of -72,525,224 bytes that way, against a live set the builder provably
/// keeps. Asking for <see cref="GCKind.FullBlocking"/> pins the reading to the same class of collection
/// this method just forced, so a before and an after are comparable by construction. Recorded in
/// https://github.com/APKiwiOrg/KhaozEngine/issues/1030.
/// </para>
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
        return GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes;
    }

    internal static long ReadTotalMemory() => GC.GetTotalMemory(forceFullCollection: true);
}
