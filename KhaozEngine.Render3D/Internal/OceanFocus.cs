using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The FFT ocean's SAMPLING FRAME: the onshore-focus rotation, the per-cascade rotation offsets, and the
    /// large-scale domain warp. The CPU mirror of the <c>oceanFocusRot</c> / <c>oceanRotAdd</c> /
    /// <c>oceanToSample</c> / <c>oceanToWorld</c> / <c>oceanWarp</c> helpers in
    /// <see cref="ShaderSources"/>'s <c>WaterFftCommonGlsl</c>, in the same op order and with the same constants,
    /// so the maths is testable headlessly the way <see cref="WaterMath"/> and <see cref="OceanSpectrum"/> are.
    /// <para>
    /// <b>None of this touches the spectrum, the transform, or the produced maps.</b> It changes only WHERE each
    /// world position reads the cascade maps, and which way the vectors it reads back point. A rotation preserves
    /// |k|, so the cascades' disjoint wave-number bands and their energies are untouched; what moves is the
    /// spectrum's directional lobe, which is exactly the point.
    /// </para>
    /// <para>
    /// The frame is a rotation angle carried as its <c>(cos, sin)</c> PAIR rather than as the angle itself,
    /// everywhere. That is not a micro-optimization: the vertex stage hands the focus rotation down to the
    /// fragment as a varying, and interpolating an angle across a triangle would sweep the long way round
    /// wherever the angle wraps, which near the focus point is every triangle. A pair interpolates to the chord,
    /// which always takes the short way and never wraps. <see cref="Renormalize"/> is the other half of that.
    /// </para>
    /// <para>
    /// <b>The focus does NOT rotate the sampling coordinate by its own angle, and that is the whole design.</b>
    /// The obvious implementation - sample at <c>R(-phi(P)) P</c> - is degenerate, provably and visibly. A
    /// rotation field that turns to face a point winds once around it, so in polar coordinates about that point
    /// its angle cancels the sample's own azimuth exactly, and the entire plane maps onto a single RAY of the
    /// map: every crest becomes a perfect circle and the sea renders as a bullseye. Backing the strength off does
    /// not fix it either, it just scales the damage - the azimuthal stretch is <c>1 / (1 - strength)</c>, which
    /// is a visible smear well before the strength is high enough for the convergence to read. It is not a tuning
    /// problem: no non-constant rotation field is integrable as a coordinate map at all (equate the mixed
    /// partials of <c>R(-phi)</c> and both components of <c>grad phi</c> fall out zero), so there is no version
    /// of this that works.
    /// </para>
    /// <para>
    /// So the heading is carried by BLENDING, in <see cref="Sectors"/>: quantize the wanted rotation to a ring of
    /// fixed lattice rotations, sample the two either side of it, and mix. Each tap is a plain constant-rotation
    /// sample and is therefore undistorted, the mix is two decorrelated realizations of the same spectrum at
    /// headings a sector apart (a directional spread, which a real sea has anyway), and the cost is two taps
    /// rather than one no matter how many sectors there are, because only ever two are non-zero.
    /// </para>
    /// </summary>
    internal static class OceanFocus
    {
        /// <summary>The identity frame: no rotation. Exactly <c>(1, 0)</c>, so <see cref="ToSampleFrame"/> and
        /// <see cref="ToWorldFrame"/> are bit-exact no-ops against it.</summary>
        public static readonly Vector2 Identity = new(1f, 0f);

        /// <summary>Squared distance to <see cref="WaterSeaState.OnshoreFocusPoint"/> below which the focus
        /// rotation is the identity. The heading toward a point is undefined AT that point, and its gradient is
        /// unbounded approaching it, so the last hair is clamped rather than left to <c>atan(0, 0)</c>. Mirrors
        /// the GLSL literal.</summary>
        public const float MinFocusDistanceSquared = 1e-8f;

        /// <summary>Tolerance on a carried pair's squared length inside which it is taken as already unit, in
        /// <see cref="Renormalize"/>. Mirrors the GLSL literal.</summary>
        public const float UnitTolerance = 1e-6f;

        /// <summary>Peak of the two-frequency warp lobe (<c>1 + 0.7</c>), divided out so
        /// <see cref="DomainWarp"/>'s amplitude is in metres of actual peak displacement rather than in units of
        /// whatever the lobe happens to sum to. Mirrors the GLSL literal.</summary>
        public const float WarpPeak = 1.7f;

        /// <summary>Second, incommensurate frequency of the domain warp, as a fraction of the first. Mirrors
        /// <see cref="WaterMath"/>'s <c>WarpFrequencyB</c> and the GLSL literal: the warp is built from two
        /// frequencies on each axis so it does not simply repeat at its own wavelength.</summary>
        public const float WarpFrequencyB = 0.57f;

        /// <summary>
        /// The onshore-focus rotation at a world XZ position, as a <c>(cos, sin)</c> pair. Turns the sampling
        /// frame so the spectrum's dominant heading (<paramref name="windRadians"/>) points from
        /// <paramref name="position"/> toward <paramref name="focus"/>, scaled by
        /// <paramref name="strength"/>.
        /// <para>
        /// <paramref name="strength"/> at or below 0 returns <see cref="Identity"/> by an EARLY RETURN, not by
        /// falling through to <c>cos(0)</c>. That is deliberate: GLSL allows <c>cos</c> a couple of ULP, so a
        /// backend returning 0.99999994 for <c>cos(0)</c> would scale every sample position by that and the
        /// unfocused surface would stop being bit-identical to the surface that has no focus feature at all.
        /// </para>
        /// <para>
        /// The angular difference is wrapped to the short way round, which is what puts the unavoidable seam of a
        /// PARTIAL focus on the ray running downwind from the focus point (see
        /// <see cref="WaterSeaState.OnshoreFocusStrength"/>). At strength 0 and at strength 1 there is no seam.
        /// </para>
        /// </summary>
        public static Vector2 FocusRotation(Vector2 position, Vector2 focus, float strength, float windRadians)
        {
            if (strength <= 0f) return Identity;
            Vector2 toFocus = focus - position;
            float d2 = toFocus.X * toFocus.X + toFocus.Y * toFocus.Y;
            if (d2 <= MinFocusDistanceSquared) return Identity;
            float delta = MathF.Atan2(toFocus.Y, toFocus.X) - windRadians;
            delta = MathF.Atan2(MathF.Sin(delta), MathF.Cos(delta));   // shortest way round
            float phi = strength * delta;
            return new Vector2(MathF.Cos(phi), MathF.Sin(phi));
        }

        /// <summary>Compose two rotations carried as <c>(cos, sin)</c> pairs: the pair for the sum of their
        /// angles, since <c>R(a + b) = R(a) R(b)</c>. Composing two <see cref="Identity"/> pairs is exactly
        /// <see cref="Identity"/> again.</summary>
        public static Vector2 Compose(Vector2 a, Vector2 b)
            => new(a.X * b.X - a.Y * b.Y, a.Y * b.X + a.X * b.Y);

        /// <summary>Rescale an interpolated <c>(cos, sin)</c> pair back to unit length. Linear interpolation
        /// across a triangle takes the CHORD between the vertices' rotations, which is the behaviour wanted (it
        /// never wraps the long way round) but is a hair short of unit, and a short pair scales the sample
        /// position as well as turning it. Pairs already within <see cref="UnitTolerance"/> are returned
        /// UNCHANGED rather than divided by a computed 1.0, so an unrotated frame stays bit-exactly
        /// <see cref="Identity"/> instead of depending on a backend's <c>inversesqrt(1.0)</c>.</summary>
        public static Vector2 Renormalize(Vector2 cs)
        {
            float l2 = cs.X * cs.X + cs.Y * cs.Y;
            if (l2 <= MinFocusDistanceSquared) return Identity;
            if (MathF.Abs(l2 - 1f) <= UnitTolerance) return cs;
            return cs * (1f / MathF.Sqrt(l2));
        }

        /// <summary>World XZ to the cascade's SAMPLING frame: <c>R(-theta)</c>. This is the direction the
        /// POSITION goes; a plane wave the map carries at wave vector k therefore lands in the world at
        /// <c>R(theta) k</c>, which is why turning the frame turns the sea.</summary>
        public static Vector2 ToSampleFrame(Vector2 xz, Vector2 cs)
            => new(cs.X * xz.X + cs.Y * xz.Y, cs.X * xz.Y - cs.Y * xz.X);

        /// <summary>A VECTOR read out of the maps (horizontal displacement, or height slope) back into the world
        /// frame: <c>R(+theta)</c>, the inverse of <see cref="ToSampleFrame"/>. Scalars the maps carry (height,
        /// foam, the Jacobian) are invariant under a rotation and pass through untouched.</summary>
        public static Vector2 ToWorldFrame(Vector2 v, Vector2 cs)
            => new(cs.X * v.X - cs.Y * v.Y, cs.Y * v.X + cs.X * v.Y);

        /// <summary>
        /// The very-large-scale STATIC warp of the sampling domain, applied before any rotation.
        /// <paramref name="amplitudeMetres"/> at or below 0 returns the position unchanged.
        /// <para>
        /// Static rather than scrolling, unlike <see cref="WaterMath.DomainWarp"/>: at a wavelength several times
        /// the largest cascade tile, a drifting warp reads as the whole sea sloshing rather than as detail. Its
        /// Jacobian is deliberately NOT folded back into the sampled slope or displacement, for the same reason
        /// the ripple warp's is not - the warp is enormously lower-frequency than anything it displaces, so
        /// treating it as a slowly varying reparametrization costs a few per cent of gradient accuracy on a
        /// stylized surface and saves the derivative terms. Keeping the stretch
        /// (<c>2 pi * amplitude / wavelength</c>) well under 1 is what makes that true, and is the documented
        /// constraint on the knob.
        /// </para>
        /// </summary>
        public static Vector2 DomainWarp(Vector2 xz, float amplitudeMetres, float wavelengthMetres)
        {
            if (amplitudeMetres <= 0f) return xz;
            float k = 2f * MathF.PI / MathF.Max(wavelengthMetres, 1f);
            float ax = MathF.Sin(xz.Y * k) + 0.7f * MathF.Sin(xz.X * k * WarpFrequencyB);
            float az = MathF.Cos(xz.X * k) + 0.7f * MathF.Cos(xz.Y * k * WarpFrequencyB);
            float amp = amplitudeMetres / WarpPeak;
            return new Vector2(xz.X + ax * amp, xz.Y + az * amp);
        }

        /// <summary>Lowest and highest <see cref="WaterSeaState.OnshoreFocusSectors"/>. Four is the coarsest
        /// setting at which "toward the focus" still means anything; past the top the sectors are finer than the
        /// spectrum's own directional spread and nothing changes.</summary>
        public const int MinSectors = 4, MaxSectors = 64;

        /// <summary>
        /// The two-tap blend that carries a per-position heading without distorting the field: the lower of the
        /// two lattice rotations either side of <paramref name="focusRotation"/>, and the blend between them.
        /// </summary>
        /// <param name="focusRotation">The WANTED rotation, from <see cref="FocusRotation"/>.</param>
        /// <param name="sectors">How many fixed lattice rotations the turn is quantized to. Costs nothing: only
        /// the two either side of the wanted rotation are ever non-zero, whatever this is.</param>
        /// <returns>
        /// <c>Lower</c> is the lower tap's rotation pair (the upper tap is that composed with one sector, which
        /// the caller has as a constant). <c>T</c> is the position between them, 0 at the lower and 1 at the
        /// upper. <c>Norm</c> scales the L2 weights: the displacement and slope maps are zero-mean Gaussian
        /// fields, so mixing two decorrelated realizations with weights <c>(1-T, T) * Norm</c> keeps the
        /// spectrum's variance exactly, where plain linear weights would dip the wave height by up to 30 per cent
        /// mid-sector. Foam is NOT a Gaussian field - it is a bounded coverage - so it takes the plain linear
        /// <c>(1-T, T)</c> instead, which preserves its mean.
        /// </returns>
        public static (Vector2 Lower, float T, float Norm) Sectors(Vector2 focusRotation, int sectors)
        {
            int n = Math.Clamp(sectors, MinSectors, MaxSectors);
            float phi = MathF.Atan2(focusRotation.Y, focusRotation.X);
            float m = phi * n / (2f * MathF.PI);
            float m0 = MathF.Floor(m);
            float t = m - m0;
            float a0 = m0 * (2f * MathF.PI / n);
            float norm = 1f / MathF.Sqrt(MathF.Max((1f - t) * (1f - t) + t * t, 1e-8f));
            return (new Vector2(MathF.Cos(a0), MathF.Sin(a0)), t, norm);
        }

        /// <summary>The identity blend: one tap at no rotation, full weight. What <see cref="Sectors"/> would
        /// return for an unrotated frame, reached WITHOUT going through it so the unfocused ocean never touches
        /// an <c>atan</c>, a <c>floor</c> or a <c>cos</c> at all.</summary>
        public static (Vector2 Lower, float T, float Norm) NoSectors => (Identity, 0f, 1f);

        /// <summary>
        /// The whole sampling-frame chain for one cascade tap at one world position, as the shaders run it: warp
        /// the position, compose the tap's lattice rotation with the cascade's own offset, and take the position
        /// into that tap's sample frame. Returns the sampling XZ and the composed rotation the caller needs to
        /// bring the sampled vectors back (<see cref="ToWorldFrame"/>).
        /// </summary>
        public static (Vector2 SampleXz, Vector2 Rotation) SampleFrame(Vector2 worldXz, Vector2 tapRotation,
            Vector2 cascadeRotation, float warpMetres, float warpWavelengthMetres)
        {
            Vector2 warped = DomainWarp(worldXz, warpMetres, warpWavelengthMetres);
            Vector2 cs = Compose(tapRotation, cascadeRotation);
            return (ToSampleFrame(warped, cs), cs);
        }
    }
}
