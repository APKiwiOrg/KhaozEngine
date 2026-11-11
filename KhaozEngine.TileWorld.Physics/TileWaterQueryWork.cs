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

    internal static void Reset()
    {
        Intersections = NonuniformVisits = IndexNodes = ConvexIntersections = BedSeamProofs = BedSeamCertificates = 0;
        Enabled = true;
    }
}
