using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The nearest ray intersection. <see cref="Body"/> is the handle of the static that was hit, or null
/// when the hit was a dynamic body (a dynamic hit has no static seam handle to report, and null is a real
/// sentinel here, not a fallback: it is never confused with the world's first-added static).</summary>
public readonly record struct RayHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle? Body);

/// <summary>The nearest swept-shape time of impact. <see cref="Body"/> is the handle of the static that was hit,
/// or null when the hit was a dynamic body (a dynamic hit has no static seam handle to report, and null is a
/// real sentinel here, not a fallback: it is never confused with the world's first-added static).</summary>
public readonly record struct SweepHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle? Body);
