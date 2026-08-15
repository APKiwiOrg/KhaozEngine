using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The rotation half of the shadow fit's quantization: decide whether the cascade fit may keep using the light
    /// direction it last fitted with, or whether the sun has turned far enough that the fit must adopt the new one.
    /// Pure math, no GPU and no engine state, so the threshold arithmetic is pinned headless by
    /// <c>ShadowLightHoldTests</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <see cref="ShadowMapMath.BuildLightViewProj"/> already snaps the focus to texel
    /// increments in light-view space, so a camera sliding by less than a texel does not move the fitted frustum and
    /// the depth pass reuses its atlas. The light DIRECTION had no equivalent treatment in the code this replaces,
    /// so a sun rotation of any size rebuilt the view basis, moved every matrix entry, and re-recorded the whole
    /// atlas. A stationary scene under a moving sun therefore repainted every caster every frame for a shadow
    /// displacement far below one texel (issue #410,
    /// docs/design/SHADOW-RERECORD-STALL-DESIGN-2026-08-12.md section 3.3). That is true of the current code and
    /// not of this repo's history: 13.1.0 shipped <c>ShadowSettings.ShadowLightQuantizeDegrees</c> plus
    /// <c>ShadowMapMath.QuantizeDirection</c>, an angular-lattice snap of the direction before the fit, for the
    /// same dirty-skip, and 14.0.0 removed both unadopted. The design doc's 3.3 prior-art paragraph says why this
    /// is a different bet: it defaults ON, it HOLDS the last adopted direction instead of snapping to a lattice
    /// (so a static sun fits byte-identically to before, which a lattice snap does not), and it ships no
    /// step-blend companion, so the moving-caster ghosting that killed the earlier family is absent by
    /// construction.
    /// </para>
    /// <para>
    /// <b>The rule, and why the naive one is wrong.</b> What a viewer sees is how far a shadow moves ON THE GROUND,
    /// and that depends on the sun's ELEVATION as much as on the rotation. A caster <c>h</c> tall at sun elevation
    /// <c>e</c> throws its foot <c>h*cot(e)</c> from its base, so an azimuth step <c>dPhi</c> sweeps that foot by
    /// <c>h*cot(e)*dPhi</c> and an elevation step <c>dE</c> slides it by <c>h*dE/sin^2(e)</c>. Those two are not
    /// comparable as written, because <c>dPhi</c> is AZIMUTH and <c>dE</c> is great-circle: an azimuth step of
    /// <c>dPhi</c> is only <c>cos(e)*dPhi</c> of great-circle travel. Per great-circle radian the pair is
    /// <c>h/sin(e)</c> for azimuth and <c>h/sin^2(e)</c> for elevation, so both exceed the naive <c>h*dTheta</c>
    /// at every elevation and the elevation term is the larger by <c>1/sin(e) &gt;= 1</c>. It is also an EXACT
    /// bound rather than merely a conservative one, because the two displacements are perpendicular on the ground
    /// (elevation slides the foot along the azimuth, azimuth sweeps it across): a rotation splitting
    /// <c>dTheta</c> between them drifts <c>h*sqrt((dE/sin^2(e))^2 + (dPhiGc/sin(e))^2)</c>, at most
    /// <c>h*dTheta/sin^2(e)</c> and reaching it on a pure elevation change. That is what lets ONE condition cover
    /// azimuth and elevation together. Against a cascade's own quantum <c>TexelWorldSize(r, res) = 2r/res</c>:
    /// <code>
    /// h_max * dTheta / sin^2(e) &lt; budget * 2r/res
    /// </code>
    /// The factor is 3.04 at a 35 degree sun and 131 at a 5 degree dusk, so a constant derived at one elevation is
    /// 43x wrong at the other and the elevation MUST be read per frame rather than baked in.
    /// </para>
    /// <para>
    /// <b>The radius is the SMALLEST active cascade's.</b> Cascade 0 is the tightest fit and therefore sets the
    /// smallest threshold. Because the dirty decision is still one bool for the whole atlas, the hold has to satisfy
    /// every cascade at once, so the caller passes the minimum radius over the active cascades. Per-cascade holding
    /// (which would let cascade 3 sit still roughly an order of magnitude longer) is option C in the design doc and
    /// is not built here.
    /// </para>
    /// </remarks>
    internal static class ShadowLightHold
    {
        /// <summary>
        /// The smallest threshold (radians) a hold decision is allowed to act on. Below this the decision would be
        /// made on noise: <c>Post.LightDirection</c> is a <see cref="Vector3"/> of floats, so a unit direction only
        /// carries about <c>1e-7</c> radians of angular resolution in the first place, and a threshold within an
        /// order of magnitude of that is comparing two rounding errors. <see cref="ShouldAdopt"/> therefore re-fits
        /// unconditionally under it.
        /// <para>
        /// This costs nothing real, because the elevation correction has already made the hold worthless wherever it
        /// bites. Where it engages is not a fixed sun angle: solving <c>budget*(2r/res)*sin^2(e)/h = 1e-5</c> for
        /// the elevation gives a standdown that scales as <c>1/sqrt(r)</c> with cascade 0's CAMERA-derived fitted
        /// radius, so it is about 2.6 degrees on a wide outdoor framing (<c>r</c> around 60 m) and about 5.8
        /// degrees at the 12 m radius <c>ShadowLightHoldTests</c> fits. Wherever it lands, the threshold there IS
        /// the floor, <c>1e-5</c> radians or about <c>5.7e-4</c> degrees, and Ruinborne's own daylight rate of
        /// <c>0.00333</c> degrees per frame is some six times that, so it crosses on every single frame anyway.
        /// What it buys is that a near-horizon sun degrades to today's re-fit-always behaviour deterministically
        /// rather than holding or releasing on float noise.
        /// </para>
        /// </summary>
        public const float MinResolvableRadians = 1e-5f;

        /// <summary>
        /// <c>sin(e)</c> for the sun elevation <c>e</c> implied by <paramref name="lightDir"/> (the direction the
        /// light TRAVELS, so a sun at elevation <c>e</c> has <c>Y = -sin(e)</c>). Returns the magnitude, so a light
        /// travelling upward is treated as the mirror elevation rather than as a negative one. Expects a normalized
        /// direction. A light along the horizon returns 0, which collapses the threshold to 0 and re-fits every
        /// frame, the conservative direction.
        /// </summary>
        public static float SinElevation(Vector3 lightDir) => MathF.Abs(lightDir.Y);

        /// <summary>
        /// The largest light rotation (radians) whose worst-case shadow displacement stays inside
        /// <paramref name="texelBudget"/> shadow texels, per the rule in the type remarks:
        /// <c>budget * (2r/res) * sin^2(e) / h_max</c>. <paramref name="minCascadeRadius"/> is the smallest active
        /// cascade's fitted slice-sphere radius, <paramref name="resolution"/> the per-cascade atlas resolution,
        /// <paramref name="sinElevation"/> the value from <see cref="SinElevation"/>, and
        /// <paramref name="maxCasterHeight"/> the worst-case caster height the budget is sized for.
        /// <para>
        /// Returns 0 (never hold) for any degenerate input: a non-positive budget, height, radius or resolution, a
        /// horizon sun, or a non-finite argument. 0 is the safe answer because a 0 threshold makes every rotation
        /// cross it, which is exactly today's re-fit-always behaviour.
        /// </para>
        /// </summary>
        public static float ThresholdRadians(float minCascadeRadius, int resolution, float sinElevation,
            float maxCasterHeight, float texelBudget)
        {
            if (!(texelBudget > 0f) || !(maxCasterHeight > 0f) || !(minCascadeRadius > 0f) || resolution <= 0) return 0f;
            if (!(sinElevation > 0f)) return 0f;
            float texelWorld = ShadowMapMath.TexelWorldSize(minCascadeRadius, resolution);
            float threshold = texelBudget * texelWorld * sinElevation * sinElevation / maxCasterHeight;
            return float.IsFinite(threshold) && threshold > 0f ? threshold : 0f;
        }

        /// <summary>
        /// Angle (radians) between two unit vectors, via the chord: <c>2*asin(|a-b|/2)</c>. Exact for unit inputs and
        /// well conditioned at the tiny angles this decision lives at, where <c>acos(dot)</c> loses most of its
        /// significant digits because the dot product sits within a float epsilon of 1.
        /// </summary>
        public static float AngleBetween(Vector3 a, Vector3 b)
        {
            float chord = (a - b).Length();
            return 2f * MathF.Asin(MathF.Min(1f, chord * 0.5f));
        }

        /// <summary>
        /// Whether the cascade fit must ADOPT <paramref name="live"/> (the current light direction) in place of
        /// <paramref name="held"/> (the direction it last fitted with). <c>true</c> means re-fit and let the existing
        /// light-matrix compare dirty the depth pass as it always has. <c>false</c> means fit from
        /// <paramref name="held"/> again, which reproduces the previous frame's matrices bit for bit on an unmoved
        /// camera and so leaves <c>ShadowPassDiagnostics.LightMatrixChanged</c> false.
        /// <para>
        /// The elevation is read as the SMALLER of the two directions' elevations, because the drift per radian is
        /// worst at the lowest elevation the interval passes through, and the angle compared is the total angle
        /// between held and live rather than an accumulated arc length: a sun that wanders and returns has not moved
        /// its shadow, and arc length would claim it had.
        /// </para>
        /// <para>
        /// A non-finite <paramref name="live"/> adopts, so a degenerate <c>LightDirection</c> reaches
        /// <see cref="ShadowMapMath.BuildLightViewProj"/>'s own fallback exactly as it does with the hold disabled.
        /// So does a threshold under <see cref="MinResolvableRadians"/>, which is a near-horizon sun.
        /// </para>
        /// </summary>
        public static bool ShouldAdopt(Vector3 held, Vector3 live, float minCascadeRadius, int resolution,
            float maxCasterHeight, float texelBudget)
        {
            if (!float.IsFinite(live.X) || !float.IsFinite(live.Y) || !float.IsFinite(live.Z)) return true;
            float sinE = MathF.Min(SinElevation(held), SinElevation(live));
            float threshold = ThresholdRadians(minCascadeRadius, resolution, sinE, maxCasterHeight, texelBudget);
            if (threshold < MinResolvableRadians) return true;
            return AngleBetween(held, live) >= threshold;
        }
    }
}
