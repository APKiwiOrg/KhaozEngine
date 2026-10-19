using System;

namespace KhaozEngine.Physics;

/// <summary>The closed set of moving convex query solids. Zero is not a shape.</summary>
public enum ConvexQueryKind { ConeCylinder = 1 }

/// <summary>The convex hull of a lower axial point and full-radius discs at RingY and UpperY.
/// Coordinates are local to a feet-origin pose. Margin is the exact Minkowski sphere radius,
/// either canonical positive zero or the existing 0.001 m touching probe.</summary>
public readonly record struct ConvexQueryShape
{
    public ConvexQueryKind Kind { get; }
    public float Radius { get; }
    public float LowerY { get; }
    public float RingY { get; }
    public float UpperY { get; }
    public float Margin { get; }
    /// <summary>False for the default value. Query backends must explicitly refuse invalid shapes.</summary>
    public bool IsValid => Kind == ConvexQueryKind.ConeCylinder && float.IsFinite(Radius) && Radius > 0 &&
        float.IsFinite(LowerY) && float.IsFinite(RingY) && float.IsFinite(UpperY) &&
        LowerY < RingY && RingY < UpperY && (Margin == 0 || Margin == 0.001f);

    public ConvexQueryShape(ConvexQueryKind kind, float radius, float lowerY, float ringY,
        float upperY, float margin = 0)
    {
        Kind = kind;
        Radius = radius;
        LowerY = lowerY;
        RingY = ringY;
        UpperY = upperY;
        Margin = margin == 0 ? 0f : margin;
        if (!IsValid) throw new ArgumentException("Invalid convex query shape.");
    }

    /// <summary>Replaces the probe margin without reconstructing or rounding any base dimension.</summary>
    public ConvexQueryShape WithMargin(float margin) => new(Kind, Radius, LowerY, RingY, UpperY, margin);
}
