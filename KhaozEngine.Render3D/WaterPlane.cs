using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One water surface queued for this frame: a rectangular region on the XZ plane at a fixed still-water
    /// height (the Gerstner swell displaces the surface around it).
    /// Presentation only; cleared each <see cref="Scene3D.Begin"/> like <see cref="ShadowBlob"/> and
    /// <see cref="GroundDecal"/>. Submit with <see cref="Scene3D.DrawWater(in WaterPlane)"/> - no request queued
    /// means no water pass runs (opt-in, byte-stable when unused).
    /// </summary>
    /// <remarks>
    /// The plane is centered at (<see cref="CenterX"/>, <see cref="SurfaceY"/>, <see cref="CenterZ"/>) and spans
    /// <see cref="HalfExtentX"/> / <see cref="HalfExtentZ"/> either side along X/Z. It is drawn as a tessellated
    /// grid (see <see cref="Internal.WaterMath.GridResolution"/>) whose vertices the vertex shader displaces by
    /// the swell, so the grid IS the wave shape rather than merely a carrier for a per-pixel normal. That grid is
    /// a FIXED vertex budget however large this plane is, spread non-uniformly toward the camera by
    /// <see cref="WaterSettings.GridFocusBias"/>; a very large plane therefore gets a very large plane's worth of
    /// resolution rather than a quadratic vertex count.
    /// <para>
    /// The LOOK comes from <see cref="PixelPostProcessSettings.Water"/>, which is the scene-wide default rather
    /// than the only option: pass a <see cref="WaterLook"/> and this plane draws with those overrides instead,
    /// leaving every other queued plane on the scene's look. That is what lets a still lake and an FFT sea share a
    /// frame. Anything backing a once-per-frame GPU resource (the sea state's bake, the depth field) or selecting
    /// the pass's geometry (the grid group) stays scene-wide, so it is not on <see cref="WaterLook"/> at all.
    /// </para>
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

        /// <summary>This plane's per-plane look overrides, or <c>null</c> (the default) to draw with the scene's
        /// <see cref="PixelPostProcessSettings.Water"/> exactly as before. Each field on the look is itself
        /// nullable, so a look states only what differs from the scene.</summary>
        public WaterLook? Look { get; }

        /// <summary>Build a water plane request. <paramref name="halfExtentZ"/> defaults to
        /// <paramref name="halfExtentX"/> (a square footprint) when omitted/negative. <paramref name="look"/>
        /// defaults to <c>null</c>, i.e. the scene-wide look, so every call site written before per-plane looks
        /// existed means exactly what it always did.</summary>
        public WaterPlane(float centerX, float surfaceY, float centerZ, float halfExtentX, float halfExtentZ = -1f,
            WaterLook? look = null)
        {
            CenterX = centerX;
            SurfaceY = surfaceY;
            CenterZ = centerZ;
            HalfExtentX = halfExtentX;
            HalfExtentZ = halfExtentZ >= 0f ? halfExtentZ : halfExtentX;
            Look = look;
        }
    }
}
