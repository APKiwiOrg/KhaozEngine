using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The nearest ray intersection.</summary>
public readonly record struct RayHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle Body);

/// <summary>The nearest swept-shape time of impact.</summary>
public readonly record struct SweepHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle Body);
