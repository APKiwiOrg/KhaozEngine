namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The procedural Gerstner swell, shared by both water stages: the component generator and the evaluator.
    /// Part of the <see cref="ShaderSources"/> partial; ShaderSources.Water.cs splices this block into both
    /// <c>WaterVert</c> and <c>WaterFrag</c>, so there is ONE copy of the swell maths and it mirrors
    /// <see cref="GerstnerWaves"/> (same generator, same op order, same constants).
    /// <para>
    /// <b>The split.</b> The vertex stage takes the offset, which is geometry and can only live on the vertices. The
    /// fragment stage takes the analytic NORMAL and the whitecap FOLD, both evaluated per pixel at the fragment's
    /// still-water position. The normal used to come from the vertex and be interpolated, and on a coarse grid that
    /// is wrong in a way no knob fixed
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/381">#381</see>): a clipmap's outer rings have 8
    /// and 16 m cells under a 42 m swell whose shortest component is 13.5 m, so the vertices undersample the normal
    /// and the rasterizer interpolates what is left into large flat facets. A normal evaluated per pixel does not
    /// depend on the grid at all, and it costs the near field nothing it could see: at the innermost ring's
    /// half-metre cells the interpolated normal was already within a few hundredths of a degree of the evaluated
    /// one.
    /// </para>
    /// <para>
    /// The fold followed in <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1100">#1100</see>, for the
    /// same reason: interpolated across a coarse ring it is linear inside each triangle, so the whitecaps it
    /// thresholds came out as triangles. Per pixel it also stops being averaged over those cells, which had been
    /// holding distant whitecap coverage well under the near field's, so the fragment eases it toward a floor as the
    /// pixel footprint grows (<see cref="WaterMath.WhitecapFoldAttenuation"/>).
    /// </para>
    /// <para>
    /// The fragment's still-water position is <c>vRefXz</c>, which under the procedural source is exactly the
    /// vertex's absolute planar position interpolated, so the shading stays attached to the geometry the vertex
    /// displaced. The displaced position would be the wrong input: the trochoidal pinch moves a point by up to a few
    /// metres, and a normal evaluated there belongs to a different part of the wave.
    /// </para>
    /// <para>
    /// Touches no texture, so it may live in functions (only SAMPLING has to stay inside <c>main</c>, see
    /// ShaderSources.WaterFft.cs). The loop is bounded by a compile-time constant with an early break on the runtime
    /// count, the form every backend's cross-compiler handles without an unroll hazard.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        const string WaterSwellCommonGlsl = @"
const float KE_GRAVITY = 9.81;           // mirrors GerstnerWaves.Gravity
const float KE_LAMBDA_DECAY = 0.685;     // mirrors GerstnerWaves.LambdaDecay
const float KE_TWO_PI = 6.28318531;
const float KE_SEED_STRIDE = 1.61803399; // mirrors GerstnerWaves.SeedStride
const int   KE_MAX_SWELL = 8;            // mirrors GerstnerWaves.MaxComponents

// Closed-form geometric sum (NOT an accumulated loop), matching GerstnerWaves.BuildComponents so the two round
// identically instead of drifting by however each happened to accumulate.
float gerstnerLambdaSum(float wavelength, int n) {
    return wavelength * (1.0 - pow(KE_LAMBDA_DECAY, float(n))) / (1.0 - KE_LAMBDA_DECAY);
}

// Component i of n, regenerated from the seven swell scalars. Mirrors GerstnerWaves.BuildComponents exactly.
void gerstnerComponent(int i, int n, float lambdaSum, out vec2 d, out float k, out float a, out float omega,
                       out float q, out float ph) {
    float fi = n > 1 ? float(i) / float(n - 1) : 0.5;
    float fan = fi * 2.0 - 1.0;
    fan *= 0.55 + 0.45 * abs(fan);        // s-curve: cluster the middle, push the edges out
    float angle = SwellParams.z + SwellParams.w * fan;
    float lambda = SwellParams.y * pow(KE_LAMBDA_DECAY, float(i));
    k = KE_TWO_PI / lambda;
    a = SwellParams.x * lambda / lambdaSum;
    omega = sqrt(KE_GRAVITY * k) * SwellShape.y;
    q = a > 1e-6 ? SwellShape.x / (k * a * float(n)) : 0.0;
    ph = SwellShape.w * float(i + 1) * KE_SEED_STRIDE;
    d = vec2(cos(angle), sin(angle));
}

// The whole swell at the still-water point aXz (ABSOLUTE world XZ): the trochoidal offset, the analytic normal of
// the displaced sheet (unit), and the fold factor for whitecaps. Mirrors GerstnerWaves.Evaluate exactly. The fold
// is the determinant of the horizontal Jacobian: 1 where undeformed, > 1 in stretched troughs, dropping toward 0 at
// compressed crests, so 1 - determinant is a physical whitecap driver, and dividing by the steepness normalizes it
// so the foam coverage knob means the same thing at any steepness. A switched-off swell returns a zero offset, a
// flat-up normal and no fold. Each stage reads only the outputs it needs and the optimizer drops the rest.
void gerstnerEvaluate(vec2 aXz, float time, out vec3 offset, out vec3 normal, out float fold) {
    offset = vec3(0.0);
    normal = vec3(0.0, 1.0, 0.0);
    fold = 0.0;
    if (!(SwellParams.x > 0.0 && SwellParams.y > 0.0)) return;
    int n = clamp(int(SwellShape.z + 0.5), 1, KE_MAX_SWELL);
    float lambdaSum = gerstnerLambdaSum(SwellParams.y, n);
    float nx = 0.0, nz = 0.0, nyLoss = 0.0;   // analytic normal accumulators
    float jxx = 0.0, jzz = 0.0, jxz = 0.0;    // horizontal Jacobian accumulators
    for (int i = 0; i < KE_MAX_SWELL; i++) {
        if (i >= n) break;
        vec2 d; float k, a, omega, q, ph;
        gerstnerComponent(i, n, lambdaSum, d, k, a, omega, q, ph);
        float phase = k * (d.x * aXz.x + d.y * aXz.y) - omega * time + ph;
        float s = sin(phase), cs = cos(phase);

        float qa = q * a;                  // horizontal orbital radius
        offset.x += qa * d.x * cs;
        offset.z += qa * d.y * cs;
        offset.y += a * s;

        float wa = k * a;                  // slope magnitude
        nx += d.x * wa * cs;
        nz += d.y * wa * cs;
        nyLoss += q * wa * s;

        float qka = q * k * a;             // == steepness / n, by construction
        jxx += qka * d.x * d.x * s;
        jzz += qka * d.y * d.y * s;
        jxz += qka * d.x * d.y * s;
    }
    vec3 nv = vec3(-nx, 1.0 - nyLoss, -nz);
    float nl = length(nv);
    normal = nl > 1e-8 ? nv / nl : vec3(0.0, 1.0, 0.0);
    float jXX = 1.0 - jxx, jZZ = 1.0 - jzz, jXZ = -jxz;
    float determinant = jXX * jZZ - jXZ * jXZ;
    fold = max(0.0, 1.0 - determinant) / max(SwellShape.x, 1e-4);
}
";
    }
}
