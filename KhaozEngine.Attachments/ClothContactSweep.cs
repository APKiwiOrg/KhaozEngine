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

    internal float MinProjection(Vector3 axis) => MathF.Min(Vector3.Dot(A, axis), MathF.Min(Vector3.Dot(B, axis), Vector3.Dot(C, axis)));
    internal float MaxProjection(Vector3 axis) => -MinProjection(-axis);
}

internal readonly record struct ClothSweepSample(float Distance, Vector3 Normal);

internal interface IClothSweepPair
{
    // A bound on the support acceleration in normalized substep time. Linear
    // cloth motion contributes no curvature.
    float Curvature { get; }
    ClothSweepSample Sample(float time);
    float Gap(float time, Vector3 normal);
}

internal static class ClothContactSweep
{
    internal const int RefinementLimit = 8;
    private const float ProjectionRoundoff = .0000001f;

    private readonly record struct Window(float Start, float End, ClothSweepSample First, ClothSweepSample Last);

    internal static ClothStepStatus Check<T>(in T pair, float required, int maxRefinements, out int refinements,
        out Vector3 openingNormal) where T : struct, IClothSweepPair
    {
        refinements = 0;
        ClothSweepSample first = pair.Sample(0), last = pair.Sample(1);
        openingNormal = first.Normal;
        if (!float.IsFinite(first.Distance) || !float.IsFinite(last.Distance) || !float.IsFinite(pair.Curvature))
            return ClothStepStatus.NonfiniteState;
        if (last.Distance < required) return ClothStepStatus.UnresolvedContact;

        // An inherited clearance deficit can be moved out as positioning bias.
        // Require the sweep to preserve at least the opening clearance.
        float threshold = MathF.Min(first.Distance, required);
        Span<Window> windows = stackalloc Window[1 + RefinementLimit];
        windows[0] = new Window(0, 1, first, last);
        int count = 1;
        while (count > 0)
        {
            Window window = windows[--count];
            if (Certifies(pair, window, window.First.Normal, threshold) ||
                Certifies(pair, window, window.Last.Normal, threshold))
                continue;
            if (refinements == maxRefinements) return ClothStepStatus.SweepWork;
            refinements++;
            float time = .5f * (window.Start + window.End);
            ClothSweepSample middle = pair.Sample(time);
            if (!float.IsFinite(middle.Distance)) return ClothStepStatus.NonfiniteState;
            if (middle.Distance < threshold - ProjectionRoundoff) return ClothStepStatus.UnresolvedContact;
            windows[count++] = new Window(time, window.End, middle, window.Last);
            windows[count++] = new Window(window.Start, time, window.First, middle);
        }
        return ClothStepStatus.Accepted;
    }

    internal static float ClosingTarget<T>(in T pair, Vector3 normal, float clearance) where T : struct, IClothSweepPair
    {
        float height = .5f * pair.Curvature;
        float start = pair.Gap(0, normal);
        float spare = MathF.Max(0, start - clearance);
        if (spare >= height) return clearance;
        // Maximum closing clearance required by the parabolic chord bound,
        // given the clearance already available at the opening end.
        float extra = MathF.Sqrt(height) - MathF.Sqrt(spare);
        return clearance + extra * extra;
    }

    internal static float Angle(Quaternion opening, Quaternion closing)
    {
        Quaternion delta = Quaternion.Conjugate(opening) * closing;
        return 2 * MathF.Atan2(new Vector3(delta.X, delta.Y, delta.Z).Length(), MathF.Abs(delta.W));
    }

    private static bool Certifies<T>(in T pair, in Window window, Vector3 normal, float required)
        where T : struct, IClothSweepPair
    {
        if (normal == Vector3.Zero) return false;
        float first = pair.Gap(window.Start, normal), last = pair.Gap(window.End, normal);
        float width = window.End - window.Start;
        float curvature = pair.Curvature * width * width;
        float time = curvature == 0 ? 0 : Math.Clamp(.5f + (first - last) / curvature, 0, 1);
        float minimum = MathF.Min(MathF.Min(first, last), float.Lerp(first, last, time) - .5f * curvature * time * (1 - time));
        return minimum >= required - ProjectionRoundoff;
    }
}

internal readonly struct ClothCapsuleSweep(ClothFacet first, ClothFacet last, ClothSupport opening, ClothSupport closing)
    : IClothSweepPair
{
    public float Curvature { get; } = CurvatureOf(opening, closing);

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
        ColliderCapsule capsule = opening.CapsuleAt(closing, time);
        var axis = new Axis(capsule);
        ChainContactTriangle.Contact hit = first.At(last, time).Triangle.Closest(axis, out _);
        double distance = Math.Sqrt(hit.Residual.LengthSquared);
        Vector3 normal = distance > 0 ? (hit.Residual * (1 / distance)).ToVector3() : Vector3.Zero;
        return new ClothSweepSample((float)distance - capsule.Radius, normal);
    }

    public float Gap(float time, Vector3 normal)
    {
        ColliderCapsule capsule = opening.CapsuleAt(closing, time);
        float support = MathF.Max(Vector3.Dot(capsule.Start, normal), Vector3.Dot(capsule.End, normal)) + capsule.Radius;
        return first.At(last, time).MinProjection(normal) - support;
    }

    internal static float CurvatureOf(in ClothSupport opening, in ClothSupport closing)
    {
        float angle = ClothContactSweep.Angle(opening.Rotation, closing.Rotation);
        float reach = MathF.Max(opening.LocalCapsule.Start.Length(), opening.LocalCapsule.End.Length());
        return reach * angle * angle;
    }
}
