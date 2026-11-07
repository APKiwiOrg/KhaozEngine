using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A water box in render-relative metres. A floor of <see cref="float.MinValue"/> is unbounded below.</summary>
/// <remarks>The retained AbsorptionPerMetre member carries legacy absorption or derived extinction for
/// WaterColumnLight bodies. Both attenuate the direct sun beam through the same packed record layout.
/// CausticLayer carries layer plus one and CausticDepth the deepest slice, both zero without a planned layer.</remarks>
internal readonly record struct WaterVolumeRecord(float MinX, float MinZ, float MaxX, float MaxZ,
    float SurfaceY, float FloorY, Vector3 AbsorptionPerMetre, float PenumbraPerMetre,
    float CausticLayer = 0f, float CausticDepth = 0f);
