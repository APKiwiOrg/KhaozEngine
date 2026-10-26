using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A water box in render-relative metres. A floor of <see cref="float.MinValue"/> is unbounded below.</summary>
internal readonly record struct WaterVolumeRecord(float MinX, float MinZ, float MaxX, float MaxZ,
    float SurfaceY, float FloorY, Vector3 AbsorptionPerMetre, float PenumbraPerMetre);
