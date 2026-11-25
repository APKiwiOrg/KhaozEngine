using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainContactPass
{
    // A quarter micrometre exceeds local direction rounding in a metre-scale
    // rig. Keep the tolerance absolute so a translated rig cannot settle early
    // on a larger correction simply because its world coordinates are coarse.
    internal const double Tolerance = 0.25e-6;

    internal static bool Moved(Offset before, Offset after) =>
        (after - before).LengthSquared > Tolerance * Tolerance;
}
