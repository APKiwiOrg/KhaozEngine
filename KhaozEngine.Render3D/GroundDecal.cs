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
    /// legacy flat fill. The noise variants animate a procedural value-noise mask over the fill, and
    /// <see cref="MoltenCracks"/> paints its own two-tone cellular field (no textures anywhere).</summary>
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
        /// <summary>Animated Voronoi/cellular crack web: a near-white hot core at each cell border falling off
        /// through an <see cref="GroundDecal.AccentColor"/> glow into the dark <see cref="GroundDecal.FillColor"/>
        /// field between cells (molten-slam aftermath, ice fracture, corruption ground). Deterministic per
        /// position. <see cref="GroundDecal.PatternSpeed"/> drives a slow per-cell breathing, not a scroll.
        /// <see cref="GroundDecal.PatternParam"/> widens/narrows the cracks. Under
        /// <see cref="GroundDecalQuality.Reduced"/> the border distance drops to the cheaper single-pass
        /// F2-F1 approximation.</summary>
        MoltenCracks = 3,
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
        /// <summary>Pattern animation rate in cycles per second (scrolls the noise field, breathes the
        /// <see cref="DecalFillPattern.MoltenCracks"/> web). Only used when <see cref="Pattern"/> is not
        /// <see cref="DecalFillPattern.Solid"/>. Default 0 = a static pattern.</summary>
        public float PatternSpeed;
        /// <summary>Pattern frequency in CELLS PER WORLD UNIT (higher = finer grain). Only used when
        /// <see cref="Pattern"/> is not <see cref="DecalFillPattern.Solid"/>. Default 0 falls back to
        /// 1 cell/unit in the shader.</summary>
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
        /// <summary>Fraction of the fill alpha painted across the ENTIRE shape regardless of the sweep
        /// (0 = legacy, fill shows only where the sweep has reached). Lets the full extent read without an
        /// outline. 0 (default) = inert.</summary>
        public float BaseFill;

        /// <summary>Second, "hot" colour for patterns that paint two-tone fields - today only
        /// <see cref="DecalFillPattern.MoltenCracks"/>, where rgb tints the crack glow (the core lifts toward
        /// white on top of it) and alpha scales the crack opacity independently of <see cref="FillColor"/>.a,
        /// so the dark field can sit near-opaque while the cracks stay bright. Ignored (and never packed) for
        /// every other pattern. <see cref="FlashAdd"/> still lifts the whole decal on top.</summary>
        public Color AccentColor;

        /// <summary>Pattern-specific shape control, meaning owned by the active <see cref="Pattern"/>. For
        /// <see cref="DecalFillPattern.MoltenCracks"/> it is the crack width in CELL-SPACE units (roughly the
        /// fraction of a cell the glow spans, 0 = the shader default 0.22). Ignored (and never packed) for
        /// patterns that define no parameter.</summary>
        public float PatternParam;

        /// <summary>Noise-modulated silhouette breakup at the analytic shape edge, for every shape and pattern:
        /// 0 (default) = the exact analytic boundary, 1 = fully eroded fingers biting up to ~35% of the shape's
        /// half-thickness inward. The erosion field is stable value noise in decal-local space (no time, no RNG),
        /// so the silhouette is identical frame to frame and across clients. Erodes first, then
        /// <see cref="FeatherWidth"/> feathers the surviving boundary. Clamped to [0,1].</summary>
        public float EdgeErosion;

        /// <summary>
        /// Project onto the virtual horizontal plane at <see cref="Center"/>.Y wherever the decal's usual paint
        /// surface is missing, instead of leaving the decal truncated at the geometry's edge. A range ring
        /// overhanging a floating island's edge keeps reading over the void rather than vanishing. false (default)
        /// keeps the legacy depth-only behaviour, byte-for-byte.
        /// </summary>
        /// <remarks>
        /// The decal still CONFORMS to any surface inside its Y band (<see cref="YTolerance"/> / <see cref="MaxStep"/>),
        /// exactly as it always did. The plane is a fallback for the two cases where that surface is not there:
        /// background (no geometry at all) and geometry outside the band (a cliff face below the decal, a lower
        /// ledge).
        /// <para>
        /// In the out-of-band case the plane is painted only where it is genuinely VISIBLE, decided by a depth
        /// comparison against the surface actually at that pixel, not by whether geometry is present. Both answers
        /// occur, and the difference is not cosmetic:
        /// </para>
        /// <list type="bullet">
        /// <item>A ring overhanging a mesa hangs at the top surface's height while the cliff recedes BELOW and behind
        /// it, so the plane is nearer and is painted. The ring crosses the cliff unbroken. (Treating "geometry is
        /// present" as "do not project" instead costs the whole screen band the cliff covers, which for a typical
        /// mesa is most of the ring's near arc.)</item>
        /// <item>A wall standing ON the decal's ground, with the decal passing under it, puts real geometry in FRONT
        /// of the plane. The plane is discarded there, so the decal is occluded rather than x-rayed through solid.</item>
        /// </list>
        /// <para>
        /// Both are pinned by <c>GroundDecalVoidGoldenTests</c> and shown in the <c>telegraph_ground_void_edge</c> and
        /// <c>telegraph_ground_void_wall</c> showcase dumps.
        /// </para>
        /// </remarks>
        public bool VoidFallback;

        /// <summary>Alpha scale applied ONLY to plane-projected pixels, so they read as projected rather than as
        /// standing on ground. 0 (default) = no dim, i.e. projected pixels match ground pixels. 1 = fully
        /// transparent. Clamped to [0,1]. Ignored unless <see cref="VoidFallback"/> is set.</summary>
        public float VoidDim;
    }
}
