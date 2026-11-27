using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal readonly record struct ClothBounds(Vector3 Min, Vector3 Max)
{
    internal static ClothBounds Enclose(ClothBounds a, ClothBounds b) =>
        new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    internal ClothBounds Expand(float amount) => new(Min - new Vector3(amount), Max + new Vector3(amount));

    internal bool Overlaps(in ClothBounds other) => Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
}

internal readonly record struct ClothFacet(Vector3 A, Vector3 B, Vector3 C)
{
    internal ClothFacet(ClothTriangle face, Vector3[] positions) : this(positions[face.A], positions[face.B], positions[face.C]) { }

    internal ClothBounds Bounds => new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));

    internal ClothFacet At(in ClothFacet close, float time) =>
        new(Vector3.Lerp(A, close.A, time), Vector3.Lerp(B, close.B, time), Vector3.Lerp(C, close.C, time));

    internal ChainContactTriangle Triangle => new(A, B, C);

    internal double MinProjection(Offset axis) =>
        Math.Min(Offset.Dot(new Offset(A), axis), Math.Min(Offset.Dot(new Offset(B), axis), Offset.Dot(new Offset(C), axis)));

    internal double MaxProjection(Offset axis) => -MinProjection(axis * -1);
}

internal readonly record struct ClothSweepSample(float Distance, Vector3 Normal, Vector3 Weights, Vector3 OpposingWeights = default);

internal readonly record struct ClothSweepImpact(float Time, Vector3 OpeningNormal, ClothSweepSample Sample, float Slack = 0)
{
    internal Vector3 Normal => Vector3.Dot(OpeningNormal, Sample.Normal) > 0 ? Sample.Normal : OpeningNormal;

    internal float ClosingCorrection(float clearance)
    {
        // Only the closing positions move. Their contribution at impact is
        // the normalized time times the corresponding material weight.
        float gap = Vector3.Dot(OpeningNormal, Sample.Normal) > 0 ? Sample.Distance : -MathF.Abs(Sample.Distance);
        return (clearance + Slack - gap) / Time;
    }
}

internal interface IClothSweepPair
{
    // A bound on the support acceleration in normalized substep time. Linear
    // cloth motion contributes no curvature.
    float Curvature { get; }
    ClothSweepSample Sample(float time);
    double Gap(float time, Vector3 normal);
}

internal static class ClothContactSweep
{
    internal const int RefinementLimit = 8;
    private const float ProjectionRoundoff = .0000001f;

    private readonly record struct Window(float Start, float End, ClothSweepSample First, ClothSweepSample Last);

    internal static ClothStepStatus Check<T>(in T pair, float required, int maxRefinements, out int refinements,
        out ClothSweepImpact impact, bool requireClearOpening = false) where T : struct, IClothSweepPair
    {
        refinements = 0;
        ClothSweepSample first = pair.Sample(0), last = pair.Sample(1);
        impact = new ClothSweepImpact(1, first.Normal, last);
        if (!float.IsFinite(first.Distance) || !float.IsFinite(last.Distance) || !float.IsFinite(pair.Curvature))
            return ClothStepStatus.NonfiniteState;
        bool needsClearOpening = requireClearOpening || required == 0;
        if (last.Distance < required || needsClearOpening && first.Distance < required)
            return ClothStepStatus.UnresolvedContact;

        // An inherited clearance deficit can be moved out as positioning bias.
        // Require the sweep to preserve at least the opening clearance.
        float threshold = MathF.Min(first.Distance, required);
        // Keep positive clearance positive and allow exact tangency at zero.
        // Sampled witnesses and interval certificates use the same floor.
        if (threshold > ProjectionRoundoff || threshold < 0)
            threshold -= ProjectionRoundoff;
        if (first.Normal == Vector3.Zero && first.Distance <= 0) return ClothStepStatus.UnresolvedContact;
        Span<Window> windows = stackalloc Window[1 + 2 * RefinementLimit];
        windows[0] = new Window(0, 1, first, last);
        int count = 1;
        for (int next = 0; next < count; next++)
        {
            Window window = windows[next];
            if (Certifies(pair, window, window.First.Normal, threshold) ||
                Certifies(pair, window, window.Last.Normal, threshold))
                continue;
            if (refinements == maxRefinements) return ClothStepStatus.SweepWork;
            refinements++;
            float time = .5f * (window.Start + window.End);
            ClothSweepSample middle = pair.Sample(time);
            if (!float.IsFinite(middle.Distance)) return ClothStepStatus.NonfiniteState;
            if (middle.Distance < threshold)
            {
                // Account for the curved support over each adjacent half-window.
                float width = window.End - window.Start;
                impact = new ClothSweepImpact(time, first.Normal, middle, pair.Curvature * width * width / 32);
                return ClothStepStatus.UnresolvedContact;
            }
            windows[count++] = new Window(window.Start, time, window.First, middle);
            windows[count++] = new Window(time, window.End, middle, window.Last);
        }
        return ClothStepStatus.Accepted;
    }

    internal static float Angle(Quaternion opening, Quaternion closing) =>
        AngularDisplacement(opening, closing).Length();

    internal static Vector3 AngularDisplacement(Quaternion opening, Quaternion closing)
    {
        Quaternion delta = closing * Quaternion.Conjugate(opening);
        var axis = new Vector3(delta.X, delta.Y, delta.Z);
        float length = axis.Length();
        if (length == 0) return Vector3.Zero;
        float angle = 2 * MathF.Atan2(length, MathF.Abs(delta.W));
        return (MathF.CopySign(angle, delta.W) / length) * axis;
    }

    internal static Quaternion Interpolate(Quaternion opening, Quaternion closing, float time)
    {
        Vector3 angular = AngularDisplacement(opening, closing);
        float angle = angular.Length();
        if (angle == 0) return opening;
        return Quaternion.CreateFromAxisAngle(angular / angle, angle * time) * opening;
    }

    private static bool Certifies<T>(in T pair, in Window window, Vector3 normal, float required)
        where T : struct, IClothSweepPair
    {
        if (normal == Vector3.Zero) return false;
        double normalLength = Math.Sqrt(new Offset(normal).LengthSquared);
        double first = pair.Gap(window.Start, normal) / normalLength;
        double last = pair.Gap(window.End, normal) / normalLength;
        double width = window.End - window.Start;
        double curvature = pair.Curvature * width * width;
        double time = curvature == 0 ? 0 : Math.Clamp(.5 + (first - last) / curvature, 0, 1);
        double minimum = Math.Min(Math.Min(first, last), first + (last - first) * time - .5 * curvature * time * (1 - time));
        return minimum >= required;
    }
}

internal readonly struct ClothCapsuleSweep(ClothFacet first, ClothFacet last, ClothSupport opening, ClothSupport closing,
    ClothHeldPaths paths, float curvature) : IClothSweepPair
{
    internal ClothCapsuleSweep(ClothSupport opening, ClothSupport closing, ClothHeldPaths paths)
        : this(default, default, opening, closing, paths, CurvatureOf(opening, closing, paths)) { }

    public float Curvature { get; } = curvature;

    internal ClothCapsuleSweep WithFacets(in ClothFacet before, in ClothFacet after) =>
        new(before, after, opening, closing, paths, Curvature);

    internal ClothBounds Bounds
    {
        get
        {
            ColliderCapsule a = opening.WorldCapsule, b = closing.WorldCapsule;
            var before = new ClothBounds(Vector3.Min(a.Start, a.End), Vector3.Max(a.Start, a.End)).Expand(a.Radius);
            var after = new ClothBounds(Vector3.Min(b.Start, b.End), Vector3.Max(b.Start, b.End)).Expand(b.Radius);
            return ClothBounds.Enclose(before, after).Expand(.125f * Curvature);
        }
    }

    public ClothSweepSample Sample(float time)
    {
        ColliderCapsule capsule = opening.CapsuleAt(closing, time, paths);
        var axis = new Axis(capsule);
        ChainContactTriangle.Contact hit = first.At(last, time).Triangle.Closest(axis, out _);
        double distance = Math.Sqrt(hit.Residual.LengthSquared);
        Vector3 normal = distance > 0 ? (hit.Residual * (1 / distance)).ToVector3() : Vector3.Zero;
        return new ClothSweepSample((float)(distance - capsule.Radius), normal, hit.Weights.ToVector3());
    }

    public double Gap(float time, Vector3 normal)
    {
        ColliderCapsule capsule = opening.CapsuleAt(closing, time, paths);
        var axis = new Offset(normal);
        double reach = capsule.Radius * Math.Sqrt(axis.LengthSquared);
        double support = Math.Max(Offset.Dot(new Offset(capsule.Start), axis), Offset.Dot(new Offset(capsule.End), axis));
        return (first.At(last, time).MinProjection(axis) - support) - reach;
    }

    private static float CurvatureOf(in ClothSupport opening, in ClothSupport closing, in ClothHeldPaths paths)
    {
        float angle = ClothContactSweep.Angle(opening.Rotation, closing.Rotation);
        return opening.CurvedCapsuleReach(closing, angle, paths);
    }
}
