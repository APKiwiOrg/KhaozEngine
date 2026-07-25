using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the water VERTEX shader's Gerstner swell: the wind-driven component generator, the
    /// trochoidal displacement, its analytic normal, and the horizontal Jacobian that drives whitecap foam
    /// (<c>WaterVert</c> in <see cref="ShaderSources"/> MUST mirror this exactly, the same contract
    /// <see cref="WaterMath"/> has with <c>WaterFrag</c>). Documents the intended math and makes it
    /// headless-unit-testable. No GPU state, no allocations.
    /// <para>
    /// A Gerstner (trochoidal) wave moves each surface point on a circle rather than only up and down, so crests
    /// pinch and troughs flatten: that is the shape a plain sum-of-sines height field cannot produce, and it is what
    /// gives the surface a real silhouette instead of a flat sheet with shading painted on it. The wave layers in
    /// <see cref="RippleSpectrum"/> ride ON TOP of this as the small-scale ripple detail; this class is only the
    /// long swell.
    /// </para>
    /// </summary>
    internal static class GerstnerWaves
    {
        /// <summary>Largest component count the generator (and the mirrored GLSL loop) supports. The GLSL loop is
        /// bounded by this constant with an early break on the runtime count, which is the form every backend's
        /// cross-compiler handles without an unroll hazard. Raised from 6 to 8 in 14.25.0: a denser ladder makes
        /// the near-field sea less regular, and the cost is one sin/cos pair per component per VERTEX, which is
        /// cheap next to the per-pixel ripple spectrum.</summary>
        public const int MaxComponents = 8;

        /// <summary>Deep-water gravity (m/s^2) for the dispersion relation below. A constant, not a knob: changing it
        /// only rescales what <see cref="WaterSettings.SwellSpeed"/> already does.</summary>
        const float Gravity = 9.81f;

        /// <summary>Geometric ratio between successive component wavelengths. Not a round number on purpose: a
        /// ratio like 0.5 would make every second component an exact harmonic of the one before it and the sum
        /// would repeat over the longest wavelength. 0.685 keeps them mutually incommensurate, so the summed swell
        /// has no short repeat, which is the same reasoning as the irrational frequency multipliers in
        /// <see cref="WaterMath"/>'s ripple layers.</summary>
        public const float LambdaDecay = 0.685f;

        const float TwoPi = 6.28318531f;

        /// <summary>Golden-ratio stride used to spread the per-component seed phases apart, so a non-zero
        /// <see cref="WaterSettings.SwellSeed"/> decorrelates two water bodies rather than shifting them all by the
        /// same amount (which would just translate the identical surface).</summary>
        const float SeedStride = 1.61803399f;

        /// <summary>
        /// One generated Gerstner component. Produced by <see cref="BuildComponents"/>, consumed by
        /// <see cref="Evaluate"/>. Values are already in the form the per-vertex loop wants (wave number, angular
        /// speed, pre-divided steepness) so the hot loop is pure multiply-add plus one sin/cos pair.
        /// </summary>
        public readonly struct Component
        {
            /// <summary>Unit travel direction, world X component.</summary>
            public readonly float DirX;
            /// <summary>Unit travel direction, world Z component.</summary>
            public readonly float DirZ;
            /// <summary>Wave number k = 2*pi / wavelength (radians per world unit).</summary>
            public readonly float WaveNumber;
            /// <summary>Vertical amplitude (world units); the crest sits this far above the still plane.</summary>
            public readonly float Amplitude;
            /// <summary>Angular speed omega (radians/second) from the deep-water dispersion relation.</summary>
            public readonly float AngularSpeed;
            /// <summary>Per-component Gerstner Q: the HORIZONTAL pinch factor, already normalized so the summed
            /// steepness across every component equals <see cref="WaterSettings.SwellSteepness"/> and the surface
            /// therefore cannot fold back through itself while that stays &lt;= 1.</summary>
            public readonly float Steepness;
            /// <summary>Constant phase offset (radians) from the seed.</summary>
            public readonly float Phase;

            /// <summary>Build a component from its already-resolved terms.</summary>
            public Component(float dirX, float dirZ, float waveNumber, float amplitude, float angularSpeed,
                float steepness, float phase)
            {
                DirX = dirX; DirZ = dirZ; WaveNumber = waveNumber; Amplitude = amplitude;
                AngularSpeed = angularSpeed; Steepness = steepness; Phase = phase;
            }
        }

        /// <summary>Displacement, normal and fold factor for one surface point. Returned by
        /// <see cref="Evaluate"/>.</summary>
        public readonly struct Sample
        {
            /// <summary>World-space offset to ADD to the still-water grid position: horizontal pinch in X/Z, wave
            /// height in Y.</summary>
            public readonly Vector3 Offset;
            /// <summary>Analytic surface normal of the displaced sheet, normalized.</summary>
            public readonly Vector3 Normal;
            /// <summary>Fold factor, normalized to roughly 0..1 by the configured steepness: 0 where the surface is
            /// locally undeformed or stretched (troughs), rising toward 1 where it is most compressed (the sharp
            /// crests a real ocean breaks on). Drives the whitecap foam mask. See <see cref="Evaluate"/>.</summary>
            public readonly float Fold;

            /// <summary>Build a sample from its three terms.</summary>
            public Sample(Vector3 offset, Vector3 normal, float fold)
            {
                Offset = offset; Normal = normal; Fold = fold;
            }
        }

        /// <summary>
        /// Generate the swell's component stack from the compact wind-driven parameterization: one direction, one
        /// spread, one base wavelength, one total amplitude, one steepness, one speed scale, one seed. Mirrors the
        /// GLSL generator in <c>WaterVert</c> exactly (same op order, same constants), so the CPU and the vertex
        /// shader agree modulo float rounding.
        /// <para>
        /// Nothing per-component is uploaded: the shader regenerates the identical stack from the same seven
        /// scalars. That keeps the UBO at two vec4s for the whole swell instead of one per component, and it means
        /// a consumer tunes wind rather than hand-authoring a wave table.
        /// </para>
        /// <para>
        /// Shape of the stack: wavelengths form a geometric ladder down from <paramref name="wavelength"/> by
        /// <see cref="LambdaDecay"/>; amplitudes are proportional to wavelength (so every component carries the same
        /// steepness and the short ones do not dominate) and are normalized so they SUM to
        /// <paramref name="amplitude"/>; directions fan across <paramref name="spreadRadians"/> either side of
        /// <paramref name="directionRadians"/> through a mild s-curve that clusters the middle components near the
        /// wind axis and pushes the outer ones to the edges of the fan; and each angular speed comes from the
        /// deep-water dispersion relation omega = sqrt(g*k), so long components genuinely travel faster than short
        /// ones the way real swell does.
        /// </para>
        /// </summary>
        /// <param name="amplitude">Summed vertical amplitude of the whole stack (world units). Peak-to-trough is
        /// about twice this where components constructively interfere. 0 or less produces a flat plane.</param>
        /// <param name="wavelength">Wavelength of the LONGEST component (world units); the rest ladder down.</param>
        /// <param name="directionRadians">Wind/travel direction as an angle in the world XZ plane (0 = +X).</param>
        /// <param name="spreadRadians">Half-angle of the directional fan either side of the wind axis.</param>
        /// <param name="steepness">Gerstner Q in 0..1: horizontal crest pinch. 0 = a pure sum of sines, 1 = the
        /// sharpest crest before the surface would fold through itself.</param>
        /// <param name="speedScale">Multiplier on the physical dispersion speed (1 = real deep-water speed).</param>
        /// <param name="seed">Decorrelates two water bodies: it offsets the component phases only, so the wind
        /// direction and the wave shape are unchanged.</param>
        /// <param name="count">Requested component count, clamped to 1..<see cref="MaxComponents"/>.</param>
        /// <param name="destination">Receives the components; must hold at least <paramref name="count"/>.</param>
        /// <returns>The number of components written (the clamped count), or 0 when
        /// <paramref name="amplitude"/> or <paramref name="wavelength"/> is not positive.</returns>
        public static int BuildComponents(float amplitude, float wavelength, float directionRadians,
            float spreadRadians, float steepness, float speedScale, float seed, int count,
            Span<Component> destination)
        {
            if (amplitude <= 0f || wavelength <= 0f) return 0;
            int n = Math.Clamp(count, 1, MaxComponents);

            // Closed-form geometric sum, NOT an accumulated loop: the GLSL mirror uses the same closed form, so the
            // two agree bit-for-bit instead of drifting by however the loop happened to round.
            float lambdaSum = wavelength * (1f - MathF.Pow(LambdaDecay, n)) / (1f - LambdaDecay);

            for (int i = 0; i < n; i++)
            {
                float fi = n > 1 ? (float)i / (n - 1) : 0.5f;          // 0..1 across the stack
                float fan = fi * 2f - 1f;                              // -1..1 across the fan
                fan *= 0.55f + 0.45f * MathF.Abs(fan);                 // s-curve: cluster the middle, push the edges out
                float angle = directionRadians + spreadRadians * fan;

                float lambda = wavelength * MathF.Pow(LambdaDecay, i);
                float k = TwoPi / lambda;
                float a = amplitude * lambda / lambdaSum;               // amplitude proportional to wavelength
                float omega = MathF.Sqrt(Gravity * k) * speedScale;
                // Q_i = steepness / (k_i * A_i * n) makes sum(Q_i * k_i * A_i) == steepness exactly, which is the
                // no-self-intersection condition (it must stay <= 1). It also makes the Jacobian below scale
                // linearly with the steepness knob, which is what lets the foam threshold be steepness-independent.
                float q = a > 1e-6f ? steepness / (k * a * n) : 0f;
                float phase = seed * (i + 1) * SeedStride;

                destination[i] = new Component(MathF.Cos(angle), MathF.Sin(angle), k, a, omega, q, phase);
            }
            return n;
        }

        /// <summary>
        /// Evaluate the whole component stack at one still-water XZ position: the trochoidal offset, the analytic
        /// normal of the displaced sheet, and the fold factor for whitecaps. Mirrors the GLSL loop in
        /// <c>WaterVert</c> exactly.
        /// <para>
        /// The fold factor is the determinant of the HORIZONTAL Jacobian of the displacement map (how much a unit
        /// patch of still water is squeezed or stretched by the horizontal pinch). It is 1 where the surface is
        /// undeformed, above 1 in stretched troughs, and drops toward 0 at compressed crests, which is exactly
        /// where a real wave breaks. <c>1 - determinant</c> is therefore a physical whitecap driver rather than a
        /// height threshold, and dividing it by the configured steepness normalizes it to roughly 0..1 so the foam
        /// coverage knob means the same thing at any steepness.
        /// </para>
        /// </summary>
        /// <param name="worldX">Still-water world X of the grid vertex.</param>
        /// <param name="worldZ">Still-water world Z of the grid vertex.</param>
        /// <param name="timeSeconds">Animation clock (frozen for a deterministic golden).</param>
        /// <param name="steepness">The same <see cref="WaterSettings.SwellSteepness"/> the components were built
        /// with, used only to normalize <see cref="Sample.Fold"/>.</param>
        /// <param name="components">The stack from <see cref="BuildComponents"/>. Empty yields a zero offset, a
        /// flat-up normal and zero fold (the flat-plane, pre-swell surface).</param>
        public static Sample Evaluate(float worldX, float worldZ, float timeSeconds, float steepness,
            ReadOnlySpan<Component> components)
        {
            float ox = 0f, oy = 0f, oz = 0f;      // trochoidal offset
            float nx = 0f, nz = 0f, nyLoss = 0f;  // analytic normal accumulators
            float jxx = 0f, jzz = 0f, jxz = 0f;   // horizontal Jacobian accumulators

            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                float phase = c.WaveNumber * (c.DirX * worldX + c.DirZ * worldZ) - c.AngularSpeed * timeSeconds + c.Phase;
                float s = MathF.Sin(phase), cs = MathF.Cos(phase);

                float qa = c.Steepness * c.Amplitude;   // horizontal orbital radius
                ox += qa * c.DirX * cs;
                oz += qa * c.DirZ * cs;
                oy += c.Amplitude * s;

                float wa = c.WaveNumber * c.Amplitude;  // slope magnitude of this component
                nx += c.DirX * wa * cs;
                nz += c.DirZ * wa * cs;
                nyLoss += c.Steepness * wa * s;

                float qka = c.Steepness * c.WaveNumber * c.Amplitude;   // == steepness / n, by construction
                jxx += qka * c.DirX * c.DirX * s;
                jzz += qka * c.DirZ * c.DirZ * s;
                jxz += qka * c.DirX * c.DirZ * s;
            }

            var n = new Vector3(-nx, 1f - nyLoss, -nz);
            float len = n.Length();
            Vector3 normal = len > 1e-8f ? n / len : Vector3.UnitY;

            // Jacobian of (x + ox, z + oz) with respect to (x, z). The cross terms are equal by symmetry, so one
            // accumulator covers both off-diagonal entries.
            float jXX = 1f - jxx, jZZ = 1f - jzz, jXZ = -jxz;
            float determinant = jXX * jZZ - jXZ * jXZ;
            float fold = MathF.Max(0f, 1f - determinant) / MathF.Max(steepness, 1e-4f);

            return new Sample(new Vector3(ox, oy, oz), normal, fold);
        }

        /// <summary>
        /// Convenience for callers that hold <see cref="WaterSettings"/> rather than a built stack: build the
        /// components into <paramref name="scratch"/> and evaluate one point. Same math as
        /// <see cref="BuildComponents"/> + <see cref="Evaluate"/>, no allocation.
        /// </summary>
        /// <param name="settings">Swell knobs (amplitude, wavelength, direction, spread, steepness, speed, seed,
        /// component count).</param>
        /// <param name="worldX">Still-water world X.</param>
        /// <param name="worldZ">Still-water world Z.</param>
        /// <param name="timeSeconds">Animation clock.</param>
        /// <param name="scratch">Component scratch buffer; must hold at least <see cref="MaxComponents"/>.</param>
        public static Sample EvaluateSettings(WaterSettings settings, float worldX, float worldZ, float timeSeconds,
            Span<Component> scratch)
        {
            int n = BuildComponents(settings.SwellAmplitude, settings.SwellWavelength,
                DegreesToRadians(settings.SwellDirectionDegrees), DegreesToRadians(settings.SwellSpreadDegrees),
                settings.SwellSteepness, settings.SwellSpeed, settings.SwellSeed, settings.SwellComponents, scratch);
            return Evaluate(worldX, worldZ, timeSeconds, settings.SwellSteepness, scratch.Slice(0, n));
        }

        /// <summary>Degrees to radians, matching the conversion the renderer applies when it packs the swell
        /// direction/spread into the UBO (the settings are in degrees because that is what a designer types; the
        /// shader only ever sees radians).</summary>
        public static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
    }
}
