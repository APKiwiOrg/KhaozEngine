using System;

namespace KhaozEngine.TileWorld.Physics;

// Opt-in thread-local work measurements, with no clocks or query policy changes.
internal static class TileWaterQueryWork
{
    [ThreadStatic] internal static bool Enabled;
    [ThreadStatic] internal static int Intersections;
    [ThreadStatic] internal static int NonuniformVisits;
    [ThreadStatic] internal static int IndexNodes;
    // One executed fraction-window evaluation, not a logical sampler request.
    [ThreadStatic] internal static int ConvexIntersections;
    // Preparation and first-use coefficient work start at zero until the prepared evaluator lands.
    [ThreadStatic] internal static int ShapePreparations;
    [ThreadStatic] internal static int LazyCoefficientFills;
    [ThreadStatic] internal static int BedSeamProofs;
    [ThreadStatic] internal static int BedSeamCertificates;

    internal static void Reset()
    {
        Intersections = NonuniformVisits = IndexNodes = ConvexIntersections = BedSeamProofs = BedSeamCertificates = 0;
        ShapePreparations = LazyCoefficientFills = 0;
        Enabled = true;
    }
}
