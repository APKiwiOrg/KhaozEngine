using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Which shadow technique the 3D scene applies. A single-choice quality selector like <see cref="AntiAliasingMode"/>,
    /// each tier trading cost against fidelity:
    /// <list type="bullet">
    /// <item><see cref="Off"/> - no shadows (the default; existing behaviour, byte-stable, no extra cost).</item>
    /// <item><see cref="Blob"/> - a soft dark elliptical ground blob under each caster. Cheap grounding for low-end
    ///   hardware: one extra depth-reconstructed ground-decal draw per caster, no shadow map, no second geometry pass.
    ///   Grounds a character without a real shadow map.</item>
    /// <item><see cref="ShadowMap"/> - a key-light directional CASCADED shadow map with PCF (casters shadow the ground
    ///   AND each other, the semi-realistic tier). Depth-only pass over instanced casters into an ortho light-space
    ///   atlas of <see cref="ShadowSettings.ShadowCascadeCount"/> concentric cascades (texel-snapped per cascade to kill
    ///   shimmer), fitted from the tight near cascade (<see cref="ShadowSettings.ShadowFocusRadius"/>) out to
    ///   <see cref="ShadowSettings.ShadowMaxDistance"/>. Sampled by picking the tightest cascade containing the fragment,
    ///   3x3 PCF + slope-scaled bias in the shared lighting so models and terrain receive identically, with an outer
    ///   edge fade so the coverage limit is invisible. Falls back to <see cref="Blob"/> only on a device that cannot
    ///   render+sample the depth target (see <see cref="ShadowSettings.ResolveFor"/>).</item>
    /// </list>
    /// Extend by adding tiers - unknown/not-yet-wired modes resolve to a safe fallback rather than throwing
    /// (see <see cref="ShadowSettings.ResolveFor"/>).
    /// </summary>
    public enum ShadowMode
    {
        /// <summary>No shadows (default). Existing render behaviour, byte-stable, no extra cost.</summary>
        Off,
        /// <summary>Soft dark ground blob under each caster (cheap grounding, low-end fallback).</summary>
        Blob,
        /// <summary>Key-light directional shadow map with PCF (semi-realistic; casters shadow the ground and each
        /// other). Degrades to <see cref="Blob"/> only on a device that cannot render+sample the depth target.</summary>
        ShadowMap,
    }

    /// <summary>
    /// The result of resolving a requested <see cref="ShadowMode"/> against device capabilities: the tier that will
    /// actually run, whether the request was degraded, and a human-readable reason. Returned by
    /// <see cref="ShadowSettings.ResolveFor"/>; the renderer reads <see cref="Effective"/>, and a diagnostics overlay
    /// (or a game's settings screen) can surface <see cref="Degraded"/>/<see cref="Reason"/> so a fallback is visible
    /// rather than silent. Immutable value.
    /// </summary>
    public readonly struct ShadowResolution : IEquatable<ShadowResolution>
    {
        /// <summary>The tier that will actually render (never a not-yet-supported mode).</summary>
        public ShadowMode Effective { get; }
        /// <summary>The tier the game asked for (before degradation).</summary>
        public ShadowMode Requested { get; }
        /// <summary>True when <see cref="Effective"/> differs from <see cref="Requested"/> (the request was clamped).</summary>
        public bool Degraded => Effective != Requested;
        /// <summary>Why the request was degraded (empty when it was not). A diagnostics/log string, not player-facing.</summary>
        public string Reason { get; }

        internal ShadowResolution(ShadowMode requested, ShadowMode effective, string reason)
        {
            Requested = requested; Effective = effective; Reason = reason ?? "";
        }

        public bool Equals(ShadowResolution other) =>
            Effective == other.Effective && Requested == other.Requested && Reason == other.Reason;
        public override bool Equals(object? obj) => obj is ShadowResolution r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(Effective, Requested, Reason);
        public static bool operator ==(ShadowResolution a, ShadowResolution b) => a.Equals(b);
        public static bool operator !=(ShadowResolution a, ShadowResolution b) => !a.Equals(b);
        public override string ToString() => Degraded ? $"{Requested}->{Effective} ({Reason})" : Effective.ToString();
    }

    /// <summary>
    /// Shadow-quality settings for the 3D scene: the <see cref="Mode"/> tier plus the blob-tier tuning parameters.
    /// Reachable as <see cref="RenderQuality.Shadows"/>; defaults to <see cref="ShadowMode.Off"/> so existing scenes
    /// are byte-stable until a game opts in. Follows the <see cref="AntiAliasing"/> precedent: pure
    /// capability-based degradation (<see cref="ResolveFor"/>) that never throws, and pure parameter-derivation
    /// helpers (<see cref="BlobFor"/>) that headless tests pin.
    /// </summary>
    public sealed class ShadowSettings
    {
        /// <summary>The shadow tier. Default <see cref="ShadowMode.Off"/> (no shadows, no cost, existing goldens
        /// byte-stable). Set from a menu, e.g. <c>Post.Quality.Shadows.Mode = ShadowMode.Blob</c>. Validate a menu
        /// choice against the device with <see cref="ResolveFor"/>.</summary>
        public ShadowMode Mode = ShadowMode.Off;

        /// <summary>Darkness of a blob at full strength (0 = invisible, 1 = fully black under the caster). The
        /// per-request strength scales this. Default a soft <c>0.5</c>.</summary>
        public float BlobOpacity = 0.5f;

        /// <summary>Blob colour (multiplied down onto the lit ground via alpha). A near-black cool tint reads as a
        /// cast shadow rather than a paint splat. Alpha is driven by <see cref="BlobOpacity"/> x request strength.</summary>
        public Color BlobColor = new(0.02f, 0.02f, 0.03f, 1f);

        /// <summary>Soft-edge width of a blob in world units (the SDF falloff band). Larger = softer, more diffuse
        /// contact shadow. Default <c>0.35</c>.</summary>
        public float BlobEdgeSoftness = 0.35f;

        /// <summary>Height (world units above the ground) at which a caster's blob has fully faded out. A jumping
        /// character's blob shrinks and lightens as it rises, vanishing at this height. Default <c>4</c>. Set &lt;= 0
        /// to disable the height fade (constant-strength blob).</summary>
        public float BlobFadeHeight = 4f;

        /// <summary>Vertical tolerance BELOW the ground plane a blob still paints onto (conform to gently dipping
        /// terrain). Feeds the decal Y-band gate. Default <c>0.3</c>.</summary>
        public float BlobGroundYTolerance = 0.3f;

        /// <summary>Vertical tolerance ABOVE the ground plane a blob still paints onto (conform to gentle rises /
        /// stepped terrain). Feeds the decal Y-band gate. Default <c>0.4</c>.</summary>
        public float BlobGroundMaxStep = 0.4f;

        // ---- ShadowMap tier (the semi-realistic key-light directional shadow map with PCF) ---------------------

        /// <summary>Shadow-map resolution per axis (a square depth texture). Default <c>2048</c>. Bigger = crisper
        /// contact shadows at more VRAM/fill cost; a low-end profile can drop to 1024 or 512. Clamped to a sane
        /// minimum. Only used when <see cref="Mode"/> resolves to <see cref="ShadowMode.ShadowMap"/>.</summary>
        public int ShadowMapResolution = 2048;

        /// <summary>Radius (world units) of the focus sphere the FIRST (tightest) cascade frames around the camera
        /// focus point. Cascade 0 covers <c>2 * ShadowFocusRadius</c> world units per axis at
        /// <see cref="ShadowMapResolution"/> texels, so a smaller radius packs more texels onto the near action
        /// (sharper contact shadows). The outer cascades grow geometrically out to <see cref="ShadowMaxDistance"/>
        /// (see <see cref="ShadowCascadeCount"/>). Default <c>16</c>. With <c>ShadowCascadeCount == 1</c> this is the
        /// only cascade, so the tier behaves like the pre-cascade single map plus the outer edge fade.</summary>
        public float ShadowFocusRadius = 16f;

        /// <summary>Number of cascaded shadow maps (concentric on the camera focus, growing coverage). Default
        /// <c>3</c>, clamped to <see cref="MinCascades"/>..<see cref="MaxCascades"/> by <see cref="ResolvedCascadeCount"/>.
        /// Cascade 0 stays tight (<see cref="ShadowFocusRadius"/>) for crisp near shadows, and each further cascade covers
        /// more world at the same per-cascade texel budget, so distant shadows exist without the near ones softening.
        /// The cascades share ONE <see cref="ShadowMode.ShadowMap"/> R32F atlas texture (side-by-side columns), each
        /// <see cref="ShadowMapResolution"/> square, so the memory is
        /// <c>ShadowCascadeCount * ShadowMapResolution^2 * 4</c> bytes (3 x 2048 = ~48 MB). A low-end profile drops the
        /// count or the resolution. <c>1</c> is the single-map path (plus the edge fade).</summary>
        public int ShadowCascadeCount = 3;

        /// <summary>Far reach (world units) of shadow coverage: the OUTERMOST cascade frames a focus sphere of this
        /// radius, and beyond it the shadow term fades smoothly to fully lit (with the outermost cascade's UV border),
        /// so the coverage limit is invisible in normal play instead of a hard box edge. An MMO-vista default of
        /// <c>130</c>. Clamped to at least <see cref="ShadowFocusRadius"/> by <see cref="ResolvedMaxDistance"/>. Only
        /// used when <see cref="ShadowCascadeCount"/> &gt; 1 (a single cascade fades at <see cref="ShadowFocusRadius"/>).</summary>
        public float ShadowMaxDistance = 130f;

        /// <summary>Ground-plane height (world Y) the shadow map's focus is fitted onto: the view-forward ray is
        /// intersected with <c>y = ShadowGroundHeight</c> to centre the limited-radius map on the ground the camera
        /// looks at (not on the eye). Set it to the scene's average ground height. Default <c>0</c>.</summary>
        public float ShadowGroundHeight = 0f;

        /// <summary>Fallback distance (world units) in front of the camera eye the focus is placed at when the view
        /// ray does NOT hit the ground plane (the camera looks along/above the horizon). Normally the ground-plane
        /// intersection is used (see <see cref="ShadowGroundHeight"/>); this only kicks in for a flat/upward view.
        /// Default <c>18</c>.</summary>
        public float ShadowFocusDistance = 18f;

        /// <summary>Constant depth bias (in light-clip depth units, ortho NDC z over the light's full near-far depth
        /// range of <c>4 * ShadowFocusRadius</c> world units) added when comparing a receiver's depth to the shadow
        /// map, to defeat self-shadow acne on lit surfaces. Too small = acne (surface shadows itself), too large =
        /// peter-panning (the shadow detaches from the caster's contact). Since <see cref="ShadowNormalOffset"/> now
        /// carries the acne defence, this stays tiny: default <c>0.0004</c> (was 0.004 - an order of magnitude smaller
        /// once the normal offset landed. The old value put ~0.25 world units of depth bias at the default radius,
        /// which peter-panned thin casters' contact shadows). See the bias-tuning note in docs/USING-KHAOZENGINE.md.</summary>
        public float ShadowConstantBias = 0.0004f;

        /// <summary>Slope-scaled depth bias: extra bias proportional to the surface's grazing angle to the light
        /// (added on top of <see cref="ShadowConstantBias"/>), so steeply-lit polygons - which span many depth units
        /// per texel - do not acne while flat-lit ones keep tight contact. With <see cref="ShadowNormalOffset"/>
        /// handling the bulk of acne, this stays small: default <c>0.0015</c> (was 0.006).</summary>
        public float ShadowSlopeBias = 0.0015f;

        /// <summary>Normal-offset shadow bias, in shadow-map TEXELS: the receiver's sample position is pushed off its
        /// surface along the geometric normal by this many texels (world-scaled by the shadow map's texel world size,
        /// so it is automatically extent-aware as <see cref="ShadowFocusRadius"/> / <see cref="ShadowMapResolution"/>
        /// change), then scaled by the grazing angle to the key light (maximal where self-shadow acne is worst, zero
        /// when the surface faces the light head-on). This is the primary acne defence, which lets the depth biases
        /// stay tiny so the shadow keeps contact with the caster's feet (no peter-panning). Default <c>2.5</c> (set
        /// <c>0</c> to disable, falling back to depth-bias-only, which peter-pans unless the depth biases are raised).</summary>
        public float ShadowNormalOffset = 2.5f;

        /// <summary>Shadow darkness (0 = shadows invisible, 1 = the key light's diffuse+spec fully removed in
        /// shadow). Multiplies ONLY the key light's contribution (fill + ambient are untouched), so a shadow reads
        /// as shade, not black. Default <c>0.85</c>.</summary>
        public float ShadowStrength = 0.85f;

        /// <summary>Minimum cascade count (the single-map path). See <see cref="ShadowCascadeCount"/>.</summary>
        public const int MinCascades = 1;
        /// <summary>Maximum cascade count (matches the fixed-size cascade arrays in the frame UBO / shaders).</summary>
        public const int MaxCascades = 4;

        /// <summary><see cref="ShadowCascadeCount"/> clamped to the supported <see cref="MinCascades"/>..<see cref="MaxCascades"/>
        /// range, so a menu value out of range degrades to the nearest valid count instead of over-running the
        /// fixed-size cascade arrays. Pure. The renderer reads this, never the raw field.</summary>
        public int ResolvedCascadeCount => Math.Clamp(ShadowCascadeCount, MinCascades, MaxCascades);

        /// <summary><see cref="ShadowMaxDistance"/> clamped so the outermost cascade never fits tighter than the near
        /// cascade (<see cref="ShadowFocusRadius"/>): a nonsensical max distance below the focus radius collapses to
        /// the focus radius (equivalent to a single cascade). Pure.</summary>
        public float ResolvedMaxDistance => MathF.Max(ShadowMaxDistance, ShadowFocusRadius);

        /// <summary>
        /// Resolve <see cref="Mode"/> against what <paramref name="caps"/> supports, so an unsupported request
        /// degrades gracefully instead of throwing or rendering nothing:
        /// <list type="bullet">
        /// <item><see cref="ShadowMode.ShadowMap"/> runs when the device reports
        ///   <see cref="GpuCapabilities.SupportsShadowMaps"/> (can render + sample the R32_Float depth target the
        ///   manual-PCF path needs); otherwise it degrades DOWN to <see cref="ShadowMode.Blob"/> with a reason. This
        ///   is the <see cref="AntiAliasing.ResolveFor"/> stance: return the best tier that actually runs, never
        ///   crash on a menu choice. (Every currently-supported backend reports the capability, so the degradation is
        ///   a safety net for a hypothetical constrained device.)</item>
        /// <item><see cref="ShadowMode.Off"/> / <see cref="ShadowMode.Blob"/> are unchanged (always available; the
        ///   blob tier needs only the existing ground-decal path).</item>
        /// </list>
        /// Pure; safe to call every frame. A diagnostics overlay can read
        /// <see cref="ShadowResolution.Degraded"/>/<see cref="ShadowResolution.Reason"/> to surface the fallback.
        /// </summary>
        public ShadowResolution ResolveFor(in GpuCapabilities caps)
        {
            switch (Mode)
            {
                case ShadowMode.ShadowMap:
                    return caps.SupportsShadowMaps
                        ? new ShadowResolution(ShadowMode.ShadowMap, ShadowMode.ShadowMap, "")
                        : new ShadowResolution(ShadowMode.ShadowMap, ShadowMode.Blob,
                            "device lacks depth-sample support for ShadowMap; using Blob");
                default:
                    return new ShadowResolution(Mode, Mode, "");
            }
        }

        /// <summary>
        /// Pure derivation of the ground blob for one caster: given the caster's ground-projected
        /// <paramref name="footprint"/> radius, the <paramref name="requestStrength"/> (0..1 multiplier from the
        /// caster), and its <paramref name="heightAboveGround"/>, return the effective (radius, alpha) after the
        /// height fade. A caster on the ground (<paramref name="heightAboveGround"/> &lt;= 0) gets the full footprint
        /// and full alpha; as it rises the blob linearly SHRINKS (to 40% radius at the fade height) and LIGHTENS (to
        /// zero alpha at the fade height), so a jumping character's shadow softens and pulls in. With
        /// <see cref="BlobFadeHeight"/> &lt;= 0 the fade is disabled (constant footprint and alpha). Headless-tested.
        /// </summary>
        public (float radius, float alpha) BlobFor(float footprint, float requestStrength, float heightAboveGround)
        {
            float baseAlpha = Math.Clamp(BlobOpacity, 0f, 1f) * Math.Clamp(requestStrength, 0f, 1f);
            float r = MathF.Max(0f, footprint);
            if (BlobFadeHeight <= 0f || heightAboveGround <= 0f)
                return (r, baseAlpha);

            // 1 at ground, 0 at (and above) the fade height.
            float t = Math.Clamp(1f - heightAboveGround / BlobFadeHeight, 0f, 1f);
            float radius = r * (0.4f + 0.6f * t);   // never smaller than 40% of the footprint while still visible
            float alpha = baseAlpha * t;            // fully gone at the fade height
            return (radius, alpha);
        }

        /// <summary>
        /// Pure: build the dark <see cref="GroundDecal"/> that renders one blob, from a request and this settings'
        /// tuning. The blob is a filled <see cref="DecalShape.Circle"/> with a near-black fill and no outline, drawn
        /// through the ground-decal path's alpha blend: the fill's alpha (from <see cref="BlobFor"/>) blends the lit
        /// ground toward the near-black <see cref="BlobColor"/> where the SDF is inside the disc, feathering across
        /// <see cref="BlobEdgeSoftness"/> - so it reads as a darkened contact patch, not a flat paint splat. Returns
        /// <c>false</c> (no decal) when the faded alpha or radius rounds to nothing (a caster above the fade height
        /// casts no blob).
        /// </summary>
        public bool TryBuildDecal(in ShadowBlob blob, out GroundDecal decal)
        {
            (float radius, float alpha) = BlobFor(blob.Radius, blob.Strength, blob.HeightAboveGround);
            if (alpha <= 0.001f || radius <= 1e-4f)
            {
                decal = default;
                return false;
            }
            var fill = BlobColor;
            decal = new GroundDecal
            {
                Shape = DecalShape.Circle,
                Center = new Vector3(blob.Position.X, blob.GroundY, blob.Position.Z),
                Rotation = 0f,
                Size = new Vector4(radius, 0f, 0f, 0f),
                FillColor = new Color(fill.R, fill.G, fill.B, alpha),
                OutlineColor = new Color(0f, 0f, 0f, 0f),      // no outline: a blob has no ring
                EdgeThickness = MathF.Max(1e-3f, BlobEdgeSoftness),
                FillFraction = 1f,                              // solid disc (no animated sweep)
                FlashAdd = 0f,
                Blend = DecalBlend.Alpha,                        // alpha blend darkens the lit ground toward BlobColor
                YTolerance = BlobGroundYTolerance,
                MaxStep = BlobGroundMaxStep,
            };
            return true;
        }
    }

    /// <summary>
    /// One shadow-caster's per-frame blob request: darken the ground under a caster centred at
    /// <see cref="Position"/> (XZ), on the ground plane <see cref="GroundY"/>, with ground-footprint
    /// <see cref="Radius"/>, scaled by <see cref="Strength"/> (0..1) and faded by
    /// <see cref="HeightAboveGround"/>. Presentation only; queued via <see cref="Scene3D.AddShadowBlob"/> and
    /// cleared each <see cref="Scene3D.Begin"/> like the other per-frame queues. Only used when the active tier
    /// resolves to <see cref="ShadowMode.Blob"/>.
    /// </summary>
    public readonly struct ShadowBlob
    {
        /// <summary>World position of the caster (Y is ignored for placement; the blob sits on
        /// <see cref="GroundY"/>). Use the caster's origin / feet.</summary>
        public Vector3 Position { get; }
        /// <summary>The ground-plane height the blob paints onto (the surface under the caster). Usually the terrain
        /// height at <see cref="Position"/> or the character controller's ground Y.</summary>
        public float GroundY { get; }
        /// <summary>The blob's ground-footprint radius in world units (follows the caster's size).</summary>
        public float Radius { get; }
        /// <summary>Per-caster strength multiplier (0..1) on the configured blob opacity. Default full via the
        /// convenience ctor.</summary>
        public float Strength { get; }
        /// <summary>How far the caster is above <see cref="GroundY"/> (world units). Drives the height fade (a
        /// jumping caster's blob shrinks and lightens). 0 = grounded.</summary>
        public float HeightAboveGround { get; }

        /// <summary>Build a blob request. <paramref name="strength"/> defaults to 1 (full),
        /// <paramref name="heightAboveGround"/> to 0 (grounded).</summary>
        public ShadowBlob(Vector3 position, float groundY, float radius, float strength = 1f, float heightAboveGround = 0f)
        {
            Position = position; GroundY = groundY; Radius = radius; Strength = strength; HeightAboveGround = heightAboveGround;
        }
    }
}
