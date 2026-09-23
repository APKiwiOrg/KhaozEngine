using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Two readings of what the process is holding, because the obvious instrument is not accurate enough
/// to judge a budget on. Against a known live set of 27,620,000 bytes built with the churn a page
/// builder produces, <c>GC.GetTotalMemory(true)</c> answered 61,105,168 and the heap after a forced
/// compacting collection answered 27,800,304 on the same run. So the live bytes after a forced compacting
/// collection are the HEADLINE and the <c>GetTotalMemory</c> delta is reported beside them rather than
/// instead of them.
/// <para>
/// THE KIND ARGUMENT IS LOAD-BEARING. <c>GetGCMemoryInfo()</c> with no argument reports the latest GC
/// OF ANY KIND, which on a loaded machine is frequently a BACKGROUND collection that finished after the
/// forced one rather than the forced one itself. Asking for <see cref="GCKind.FullBlocking"/> pins the
/// reading to the same class of collection this method just forced, so a before and an after are
/// comparable by construction. Recorded in https://github.com/APKiwiOrg/KhaozEngine/issues/1030.
/// </para>
/// <para>
/// SO IS THE SUBTRACTION. <c>HeapSizeBytes</c> counts the FREE space inside the heap as well as the live
/// objects, and a compacting collection only squeezes that free space out of the small object heap. The
/// large object heap is swept in place, so the free gaps an earlier test's dead arrays leave there stay in
/// the figure, a new large array lands in one of them without the heap growing, and a region the
/// collector hands back takes its gaps with it. Built on purpose, those two states read 16 MiB of retained
/// blocks as a delta of 184 bytes and a 16 MiB retention as 34 MB released, the shapes of the full suite
/// failures in https://github.com/APKiwiOrg/KhaozEngine/issues/1043 and
/// https://github.com/APKiwiOrg/KhaozEngine/issues/1018. Taking <c>FragmentedBytes</c> away leaves the
/// bytes the collection found alive, which is what a retention budget asks, and the free space earlier
/// work left behind no longer moves it. Across seven full suite runs the live deltas of budget 9 and the
/// scale test matched to the byte and budget 12's within 12 KB, while the heap size deltas moved with the
/// gaps.
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
        GCMemoryInfo collection = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return collection.HeapSizeBytes - collection.FragmentedBytes;
    }

    internal static long ReadTotalMemory() => GC.GetTotalMemory(forceFullCollection: true);
}
