using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Interpolate a held joint's offset in its moving anchor, rather than taking a world-space chord.
internal readonly record struct ClothLowerArc(Quaternion AnchorRotation, Vector3 AnchorOrigin, Vector3 LocalStart)
{
    internal bool IsActive => AnchorRotation != default;

    internal static ClothLowerArc From(in ChainPose anchor, Vector3 start) =>
        new(anchor.Rotation, anchor.Matrix.Translation,
            Vector3.Transform(start - anchor.Matrix.Translation, Quaternion.Conjugate(anchor.Rotation)));

    internal Vector3 At(in ClothLowerArc after, float time) =>
        Vector3.Lerp(AnchorOrigin, after.AnchorOrigin, time) +
        Vector3.Transform(Vector3.Lerp(LocalStart, after.LocalStart, time),
            ClothContactSweep.Interpolate(AnchorRotation, after.AnchorRotation, time));

    internal float CurvatureTo(in ClothLowerArc after)
    {
        float angle = ClothContactSweep.Angle(AnchorRotation, after.AnchorRotation);
        return MathF.Max(LocalStart.Length(), after.LocalStart.Length()) * angle * angle +
            2 * angle * Vector3.Distance(LocalStart, after.LocalStart);
    }

    internal Vector3 ClosingDerivative(in ClothLowerArc before, Vector3 currentRoot) =>
        AnchorOrigin - before.AnchorOrigin + Vector3.Transform(LocalStart - before.LocalStart, AnchorRotation) +
        Vector3.Cross(ClothContactSweep.AngularDisplacement(before.AnchorRotation, AnchorRotation), currentRoot - AnchorOrigin);

    internal ClothLowerArc Rebased(Vector3 change) => IsActive ? this with { AnchorOrigin = AnchorOrigin + change } : this;
}


// An already posed upstream segment contributes an oriented offset to a later joint's sweep.
internal readonly record struct ClothHeldEdge(Quaternion Rotation, Vector3 LocalAxis)
{
    internal static ClothHeldEdge From(in HeldChainSegment segment) => new(segment.Rotation,
        Vector3.Transform(segment.End - segment.Start, Quaternion.Conjugate(segment.Rotation)));

    internal Vector3 At(in ClothHeldEdge after, float time) =>
        Vector3.Transform(Vector3.Lerp(LocalAxis, after.LocalAxis, time),
            ClothContactSweep.Interpolate(Rotation, after.Rotation, time));

    internal float CurvatureTo(in ClothHeldEdge after)
    {
        float angle = ClothContactSweep.Angle(Rotation, after.Rotation);
        return MathF.Max(LocalAxis.Length(), after.LocalAxis.Length()) * angle * angle +
            2 * angle * Vector3.Distance(LocalAxis, after.LocalAxis);
    }

    internal Vector3 ClosingDerivative(in ClothHeldEdge before) =>
        Vector3.Transform(LocalAxis - before.LocalAxis, Rotation) +
        Vector3.Cross(ClothContactSweep.AngularDisplacement(before.Rotation, Rotation), Vector3.Transform(LocalAxis, Rotation));
}


internal readonly record struct ClothHeldPaths(ClothHeldEdge[]? Opening, ClothHeldEdge[]? Closing)
{
    internal ClothHeldPaths ClosingOnly => new(Closing, Closing);
}
