using System;

namespace KhaozEngine.TileWorld.Physics;

// Opt-in thread-local work measurements, with no clocks or query policy changes.
internal static class TileWaterQueryWork
{
    [ThreadStatic] internal static bool Enabled;
    [ThreadStatic] internal static int Intersections;
    [ThreadStatic] internal static int NonuniformVisits;
    [ThreadStatic] internal static int IndexNodes;
    [ThreadStatic] internal static int ConvexIntersections;
    [ThreadStatic] internal static int BedSeamProofs;
    [ThreadStatic] internal static int BedSeamCertificates;
    [ThreadStatic] internal static int PrismTraces;
    [ThreadStatic] internal static int MemoHits;
    [ThreadStatic] internal static int MemoMisses;
    [ThreadStatic] internal static ulong KernelStatusForTests;
    [ThreadStatic] internal static bool KernelStatusCapturedForTests;

    internal static void Reset()
    {
        Intersections = NonuniformVisits = IndexNodes = ConvexIntersections = BedSeamProofs = BedSeamCertificates = 0;
        PrismTraces = MemoHits = MemoMisses = 0;
        KernelStatusForTests = 0;
        KernelStatusCapturedForTests = false;
        Enabled = true;
    }
}
