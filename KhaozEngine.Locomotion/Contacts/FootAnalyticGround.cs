using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The exact analytic height and normal sampled once at the queried axis.</summary>
internal readonly record struct FootAnalyticGround(float Height, Vector3 Normal);
