using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One flat water surface queued for this frame: a rectangular region on the XZ plane at a fixed world height.
    /// Presentation only; cleared each <see cref="Scene3D.Begin"/> like <see cref="ShadowBlob"/> and
    /// <see cref="GroundDecal"/>. Submit with <see cref="Scene3D.DrawWater(in WaterPlane)"/> - no request queued
    /// means no water pass runs (opt-in, byte-stable when unused).
    /// </summary>
    /// <remarks>
    /// The plane is centered at (<see cref="CenterX"/>, <see cref="SurfaceY"/>, <see cref="CenterZ"/>) and spans
    /// <see cref="HalfExtentX"/> / <see cref="HalfExtentZ"/> either side along X/Z. It is drawn as an
    /// axis-aligned tessellated grid (see <see cref="Internal.WaterMath.GridResolution"/>) so the animated normal
    /// perturbation has enough vertices to read as a wavy surface rather than a flat-shaded quad; the wave motion
    /// itself is entirely per-pixel (fragment) so the CPU tessellation only needs to be "screen-space sufficient",
    /// not simulation-accurate.
    /// </remarks>
    public readonly struct WaterPlane
    {
        /// <summary>World X of the plane's center.</summary>
        public float CenterX { get; }
        /// <summary>World Z of the plane's center.</summary>
        public float CenterZ { get; }
        /// <summary>World Y (height) of the flat water surface.</summary>
        public float SurfaceY { get; }
        /// <summary>Half-width along X (world units); the plane spans [CenterX-HalfExtentX, CenterX+HalfExtentX].</summary>
        public float HalfExtentX { get; }
        /// <summary>Half-width along Z (world units); the plane spans [CenterZ-HalfExtentZ, CenterZ+HalfExtentZ].</summary>
        public float HalfExtentZ { get; }

        /// <summary>Build a water plane request. <paramref name="halfExtentZ"/> defaults to
        /// <paramref name="halfExtentX"/> (a square footprint) when omitted/negative.</summary>
        public WaterPlane(float centerX, float surfaceY, float centerZ, float halfExtentX, float halfExtentZ = -1f)
        {
            CenterX = centerX;
            SurfaceY = surfaceY;
            CenterZ = centerZ;
            HalfExtentX = halfExtentX;
            HalfExtentZ = halfExtentZ >= 0f ? halfExtentZ : halfExtentX;
        }
    }
}
