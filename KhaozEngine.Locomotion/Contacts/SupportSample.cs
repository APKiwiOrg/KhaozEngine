using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>What the footprint found. <see cref="Refused"/> means a proposal could not be certified above the
/// best certified support, so no height may be trusted.</summary>
internal enum SupportStatus : byte { None, Walkable, Steep, Refused }

/// <summary>Certified support under a body's axis. The true support height lies within
/// <see cref="HeightError"/> of <see cref="Height"/>. <see cref="Static"/> null means analytic terrain.
/// <see cref="Witness"/> is the touched point inside the footprint, never the feet. Without support,
/// <see cref="Height"/> and <see cref="HeightError"/> are NaN.</summary>
internal readonly record struct SupportSample(SupportStatus Status, float Height, float HeightError,
    Vector3 Normal, StaticHandle? Static, int FeatureId, Vector3 Witness);
