using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Which analytic shape a <see cref="GroundDecal"/> paints. The SDF for each is in the decal shader.</summary>
    public enum DecalShape { Circle, Ring, Beam, Cone, Arc }

    /// <summary>Blend for a ground decal (matches the decal pipeline's two variants).</summary>
    public enum DecalBlend { Alpha, Additive }

    /// <summary>How a <see cref="GroundDecal"/>'s fill is textured by the decal shader. Mirrors the telegraph-side
    /// fill pattern the way <see cref="DecalBlend"/> mirrors the telegraph blend. <see cref="Solid"/> (0) is the
    /// legacy flat fill. The noise variants animate a procedural value-noise mask over the fill (no textures).</summary>
    public enum DecalFillPattern
    {
        /// <summary>Flat fill, no procedural texture (legacy default).</summary>
        Solid = 0,
        /// <summary>Domain-warped value noise drifting in decal-local XZ (wispy filament look, not round
        /// scrolling blobs).</summary>
        ScrollingNoise = 1,
        /// <summary>Cartesian vortex swirl: spiral arms orbiting the decal center over time, no polar
        /// singularity at the center.</summary>
        RadialNoise = 2,
    }

    /// <summary>Quality tier for the ground-decal pass, folded into the pass's Frame uniform. <see cref="Reduced"/>
    /// drops the second noise octave and the edge sparkle so weak GPUs pay less fill cost. The base fill, feather,
    /// rim, and sweep energy are unchanged.</summary>
    public enum GroundDecalQuality
    {
        /// <summary>Full fidelity: both noise octaves and edge sparkle.</summary>
        Full = 0,
        /// <summary>Cheaper: single noise octave, no edge sparkle.</summary>
        Reduced = 1,
    }

    /// <summary>
    /// One generic shaped ground decal queued for this frame: a flat shape painted onto the ground/terrain by
    /// reconstructing the surface position from the depth buffer. Presentation only; cleared each
    /// <see cref="Scene3D.Begin"/>. The higher-level telegraph wrappers (KhaozEngine.Telegraphs.Render3D) build
    /// these from a TelegraphStyle + progress.
    /// </summary>
    /// <remarks>
    /// <see cref="Size"/> packs per-shape params: Circle (x=radius); Ring (x=innerR, y=outerR);
    /// Beam (x=halfLength, y=halfWidth, oriented by <see cref="Rotation"/> about +Y from +X);
    /// Cone (x=range, y=halfAngleRad, axis from <see cref="Rotation"/>); Arc (x=radius, y=halfBandWidth,
    /// z=startAngle, w=sweepAngle). <see cref="Center"/>.Y is the ground plane height; the decal paints surfaces
    /// whose reconstructed world Y is within [Center.Y - <see cref="YTolerance"/>, Center.Y + <see cref="MaxStep"/>].
    /// </remarks>
    public struct GroundDecal
    {
        public DecalShape Shape;
        public Vector3 Center;
        public float Rotation;
        public Vector4 Size;
        public Color FillColor;
        public Color OutlineColor;
        public float EdgeThickness;
        public float FillFraction;
        public float FlashAdd;
        public DecalBlend Blend;
        public float YTolerance;
        public float MaxStep;

        /// <summary>Soft-edge half-width in WORLD UNITS added around the fill and outline boundaries (0 = the legacy
        /// hard fwidth-AA edge). Larger values feather the shape into the ground. Default 0 keeps flat rendering.</summary>
        public float FeatherWidth;
        /// <summary>Procedural fill texture applied by the decal shader. <see cref="DecalFillPattern.Solid"/> (the
        /// default) is the legacy flat fill. The noise variants modulate the fill alpha with animated value noise.</summary>
        public DecalFillPattern Pattern;
        /// <summary>Pattern animation rate in cycles per second (scrolls the noise field). Only used when
        /// <see cref="Pattern"/> is a noise variant. Default 0 = a static noise field.</summary>
        public float PatternSpeed;
        /// <summary>Pattern frequency in NOISE CELLS PER WORLD UNIT (higher = finer grain). Only used when
        /// <see cref="Pattern"/> is a noise variant. Default 0 falls back to 1 cell/unit in the shader.</summary>
        public float PatternScale;
        /// <summary>Rim glow energy: brightens a band straddling the full shape boundary toward the outline colour,
        /// with a subtle time shimmer. 0 (default) = inert.</summary>
        public float RimGlow;
        /// <summary>Sweep glow energy: a leading-edge glow tracking the animated (swept) fill boundary. 0 (default) =
        /// inert.</summary>
        public float SweepGlow;
        /// <summary>Edge sparkle energy: brief twinkles along the shape boundary (dropped under
        /// <see cref="GroundDecalQuality.Reduced"/>). 0 (default) = inert.</summary>
        public float Sparkle;
        /// <summary>How much the deep fill interior dims relative to the boundary and sweep front
        /// (0 = legacy uniform fill, 1 = fully hollow). Concentrates energy at the rim. 0 (default) = inert.</summary>
        public float InteriorDim;
        /// <summary>Rotating outline dash-runner energy: dash segments orbiting the outline band.
        /// 0 (default) = inert.</summary>
        public float Runner;
    }
}
