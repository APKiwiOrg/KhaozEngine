using System;
using System.Globalization;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The two GLSL 450 compute kernels behind <see cref="WaterWaveSource.FftOcean"/>, and the source
    /// substitution that specializes them to a resolution.
    /// <para>
    /// <b>Two kernels, not fourteen.</b> The obvious shape - one dispatch per radix-2 FFT stage, ping-ponging two
    /// buffers - costs an <c>End + Submit + WaitForIdle</c> per stage, because the seam has no cross-dispatch
    /// barrier (see <c>docs/design/GPU-COMPUTE-DESIGN-2026-07-26.md</c> and issue #311). At 128 points that is 14
    /// full GPU drains per transform per cascade, which is not a frame budget. So each AXIS is one dispatch that
    /// keeps its whole line in workgroup shared memory and runs every butterfly stage with <c>barrier()</c>
    /// between them, and the work either side of the transform is FUSED into the same two dispatches: the
    /// spectrum's time evolution rides the row pass, and the displacement/derivative/foam map assembly rides the
    /// column pass. That left one dependency (the column pass reads what the row pass wrote) and one stall per
    /// frame; #398 then removed the last one by PING-PONGING the work buffer across the frame boundary, so the
    /// column pass consumes the previous frame's rows and a steady-state frame drains the device not at all. See
    /// <see cref="OceanFrameClock"/> for the one-frame time compensation that keeps the surface phase unchanged.
    /// </para>
    /// <para>
    /// <b>Four complex fields per cascade, carrying eight real ones.</b> Every field is Hermitian, so an inverse
    /// transform of <c>A + iB</c> returns <c>a + ib</c> with both halves real and usable. Packed that way, height,
    /// both horizontal displacements, both slopes and all three displacement derivatives (for the Jacobian) come
    /// out of four transforms instead of eight, and the normals and the foam are ANALYTIC rather than finite
    /// differences of the height map.
    /// </para>
    /// <para>
    /// <b>Decimation-in-time, in place, not Stockham.</b> 15.2.0's proof kernel is Stockham, which needs two
    /// buffers; in shared memory that doubles the footprint, and four fields at 256 points would want 16 KB - the
    /// exact guaranteed minimum, with nothing left over. In-place Cooley-Tukey with a bit-reversed LOAD needs one
    /// buffer (8 KB at 256, 4 KB at 128) and the permutation is free, because the load is already a gather.
    /// </para>
    /// <para>
    /// <b>Literal workgroup sizes.</b> Compute specialization constants are not exposed by the seam (#312), so the
    /// resolution is substituted into the source and a resolution change rebuilds the pipeline. That is the
    /// documented escape hatch, and the numbers a caller would otherwise have to repeat are read back off
    /// <c>IGpuComputeShader.ThreadGroupSizeX</c> anyway.
    /// </para>
    /// <para>
    /// <b>Resources are FIRST REFERENCED in binding order, and that is load-bearing.</b> Metal has no binding
    /// decorations, so the cross-compiler assigns each resource a <c>[[buffer(n)]]</c> index of its own, in
    /// SPIR-V id order - which follows where each resource is first referenced across the emitted function
    /// bodies, and a helper function is emitted before <c>main</c>. The backend, meanwhile, binds a resource set
    /// by counting the resource layout in binding order. Get the two out of step and the kernel reads the wrong
    /// resource, on Metal only, with Vulkan and Direct3D11 perfectly correct because they honour the decorations.
    /// It happened here: the row pass read <c>H0</c> inside a helper before anything read <c>Params</c>, so the
    /// kernel took its cascade tile size out of the spectrum buffer, got 0, divided by it, and produced a NaN
    /// surface. Hence <c>packedFields</c> reads <c>Timing</c> before <c>H0</c>, and the column pass touches its
    /// three buffers only from <c>main</c>. <c>ShaderValidation.ValidateCompute</c> now rejects the mismatch in
    /// the GPU-free lane, and <c>OceanFftShaderValidationTests</c> keeps the real broken source as its negative
    /// case.
    /// </para>
    /// </summary>
    internal static class OceanComputeShaders
    {
        /// <summary>Complex fields transformed per cascade. Mirrored by <c>OceanFftProducer</c>'s buffer sizing and
        /// by the CPU reference in the tests.</summary>
        public const int Fields = 4;

        /// <summary>The row pass for a given resolution: evolve the baked spectrum to the current time, pack the
        /// four complex fields, and inverse-transform along X.</summary>
        public static string RowPass(int resolution) => Specialize(RowPassTemplate, resolution);

        /// <summary>The column pass for a given resolution: inverse-transform along Z, then assemble the
        /// displacement / derivative / foam maps for the column it owns.</summary>
        public static string ColumnPass(int resolution) => Specialize(ColumnPassTemplate, resolution);

        /// <summary>Workgroup size (threads on X) both kernels use at a resolution: one thread per butterfly, so
        /// half the transform length.</summary>
        public static uint GroupSize(int resolution) => (uint)(Validate(resolution) / 2);

        /// <summary>log2 of a validated resolution: the number of butterfly stages per axis.</summary>
        public static int Stages(int resolution)
        {
            int n = Validate(resolution), s = 0;
            while ((1 << s) < n) s++;
            return s;
        }

        /// <summary>Throw unless <paramref name="resolution"/> is a power of two within the supported range. The
        /// producer clamps before it gets here; this is the last line of defence against a source substitution
        /// that would compile but transform garbage.</summary>
        public static int Validate(int resolution)
        {
            if (resolution < OceanSpectrum.MinResolution || resolution > OceanSpectrum.MaxResolution
                || (resolution & (resolution - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution,
                    $"FFT ocean resolution must be a power of two in [{OceanSpectrum.MinResolution}, {OceanSpectrum.MaxResolution}]");
            }
            return resolution;
        }

        static string Specialize(string template, int resolution)
        {
            int n = Validate(resolution);
            var inv = CultureInfo.InvariantCulture;
            return template
                .Replace("@N@", n.ToString(inv))
                .Replace("@HALF@", (n / 2).ToString(inv))
                .Replace("@NN@", (n * n).ToString(inv))
                .Replace("@STAGES@", Stages(n).ToString(inv))
                .Replace("@HALFF@", (n * 0.5f).ToString("0.0", inv))
                .Replace("@NF@", ((float)n).ToString("0.0", inv));
        }

        // The parameter block, declared identically in both kernels. std140-clean: five vec4s, 80 bytes, mirrored
        // by OceanFftProducer.OceanUbo.
        const string ParamsGlsl = @"layout(set = 0, binding = 0) uniform Params {
    vec4 Cascade[3];   // per cascade: x = tile metres, y/z unused here (the band is baked into H0), w unused
    vec4 Timing;       // x = time seconds, y = delta seconds, z = choppiness, w = depth metres
    vec4 Foaming;      // x = foam gain, y = jacobian bias, z = dissipation per second, w = cascade count
};";

        // Shared helpers. `mh` rather than `half` on purpose: `half` is a reserved type in HLSL and MSL, and this
        // source is cross-compiled to both.
        const string CommonGlsl = @"const float KE_TWO_PI = 6.28318530717958648;
const float KE_GRAVITY = 9.81;
const float KE_TANH_LIMIT = 20.0;   // mirrors OceanSpectrum.TanhArgumentLimit

shared vec2 Line[@N@ * 4];   // four complex fields, one full transform line each, in place

vec2 cmul(vec2 a, vec2 b) { return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x); }

// Decimation-in-time radix-2, in place over `Line`, run by every thread in the workgroup. Reads for a stage are
// completed by the first barrier before any write lands, so the in-place update is safe without a second buffer.
// The twiddle sign is POSITIVE and there is no 1/N: this is the unnormalized INVERSE transform
// h(x) = sum_k h~(k) exp(+i k.x), which is the form Tessendorf's amplitudes are defined against.
void butterflies(uint t) {
    for (int s = 0; s < @STAGES@; s++) {
        uint mh = 1u << uint(s);
        uint k = t / mh;
        uint j = t - k * mh;
        uint i0 = k * 2u * mh + j;
        uint i1 = i0 + mh;

        float ang = KE_TWO_PI * 0.5 * float(j) / float(mh);
        vec2 w = vec2(cos(ang), sin(ang));

        vec2 a0 = Line[i0], b0 = Line[i1];
        vec2 a1 = Line[@N@u + i0], b1 = Line[@N@u + i1];
        vec2 a2 = Line[2u * @N@u + i0], b2 = Line[2u * @N@u + i1];
        vec2 a3 = Line[3u * @N@u + i0], b3 = Line[3u * @N@u + i1];
        barrier();

        vec2 w0 = cmul(w, b0), w1 = cmul(w, b1), w2 = cmul(w, b2), w3 = cmul(w, b3);
        Line[i0] = a0 + w0;               Line[i1] = a0 - w0;
        Line[@N@u + i0] = a1 + w1;        Line[@N@u + i1] = a1 - w1;
        Line[2u * @N@u + i0] = a2 + w2;   Line[2u * @N@u + i1] = a2 - w2;
        Line[3u * @N@u + i0] = a3 + w3;   Line[3u * @N@u + i1] = a3 - w3;
        barrier();
    }
}

// Bit-reversed index, so the in-place decimation-in-time above ends in natural order.
uint reversed(uint i) { return bitfieldReverse(i) >> (32u - uint(@STAGES@)); }
";

        /// <summary>
        /// Pass 1: one workgroup per (spectrum row, cascade). Evolves the baked <c>h0</c> to the current time,
        /// derives the four packed complex fields, and inverse-transforms along X into the work buffer.
        /// </summary>
        const string RowPassTemplate = @"#version 450
layout(local_size_x = @HALF@) in;
" + ParamsGlsl + @"
layout(std430, set = 0, binding = 1) buffer H0Buf { vec4 H0[]; };
layout(std430, set = 0, binding = 2) buffer WorkBuf { vec2 Work[]; };
" + CommonGlsl + @"
// One texel's four packed fields at time t. Every one is Hermitian, so each packed pair survives the transform as
// two independent real fields:
//   f0 = h~ + i.Dx~            -> height, x displacement
//   f1 = Dz~ + i.dh/dx~        -> z displacement, x slope
//   f2 = dh/dz~ + i.dDx/dx~    -> z slope, x-displacement gradient
//   f3 = dDz/dz~ + i.dDx/dz~   -> the remaining two Jacobian terms
void packedFields(uint c, uint m, uint n, float tile, float t,
                  out vec2 f0, out vec2 f1, out vec2 f2, out vec2 f3) {
    f0 = vec2(0.0); f1 = vec2(0.0); f2 = vec2(0.0); f3 = vec2(0.0);

    // Params is touched BEFORE H0, and that order is load-bearing rather than stylistic: see the binding-order
    // note on this class, and OceanFftShaderValidationTests, which fails the fast lane if it is ever reversed.
    float depth = Timing.w;
    vec4 h = H0[c * @NN@u + n * @N@u + m];
    float dk = KE_TWO_PI / tile;
    float kx = (float(m) - @HALFF@) * dk;
    float kz = (float(n) - @HALFF@) * dk;
    float k = sqrt(kx * kx + kz * kz);
    if (k < 1e-6) { return; }

    // The tanh argument is CLAMPED, and that clamp is load-bearing rather than tidy. A hardware tanh is commonly
    // evaluated as (exp(2x) - 1) / (exp(2x) + 1), which overflows to inf/inf = NaN well before the argument does:
    // the finest cascade reaches k*depth of 140-plus in a 60 metre sea, and on Metal that produced a NaN surface
    // for that cascade alone while the coarse one was perfect. tanh is 1 to well under a float ULP past about 10,
    // so clamping changes no value anyone can observe. KE_TANH_LIMIT mirrors OceanSpectrum.TanhArgumentLimit.
    float omega = sqrt(KE_GRAVITY * k * (depth <= 0.0 ? 1.0 : tanh(min(k * depth, KE_TANH_LIMIT))));
    // Sign is load-bearing (KhaozEngine#342): the spatial reconstruction below is a POSITIVE-twiddle inverse
    // transform (Re[h~ * e^{i k.x}]), so a term evolved as h0(k) * e^{+i omega t} produces Re[h0 e^{i(k.x + omega
    // t)}], whose crests travel along MINUS k as t grows - i.e. wind+180. Negating t here instead gives h0(k) *
    // e^{-i omega t}, whose term is Re[h0 e^{i(k.x - omega t)}] and travels along +k, matching both
    // WaterSeaState.WindDirectionDegrees's documented convention and the Procedural/Gerstner path's
    // `phase = k.d.x - omega*t` (ShaderSources.Water.cs). cw is untouched (cosine is even in t).
    float cw = cos(omega * t), sw = -sin(omega * t);
    // h~(k,t) = h0(k) e^{-i omega t} + conj(h0(-k)) e^{+i omega t}; h.zw is already conj(h0(-k)). Hermitian
    // symmetry survives the sign flip (h~(-k,t) = conj(h~(k,t)) either way), so the field stays real.
    vec2 ht = cmul(h.xy, vec2(cw, sw)) + cmul(h.zw, vec2(cw, -sw));

    float ik = 1.0 / k;
    float a = ht.x, b = ht.y;
    float cx = kx * ik, cz = kz * ik;
    float exx = kx * kx * ik, ezz = kz * kz * ik, exz = kx * kz * ik;

    f0 = vec2(a + cx * a, b + cx * b);
    f1 = vec2(cz * b - kx * a, -(cz * a + kx * b));
    f2 = (kz + exx) * vec2(-b, a);
    f3 = vec2(ezz * a - exz * b, ezz * b + exz * a);
}

void main() {
    uint t = gl_LocalInvocationID.x;
    uint n = gl_WorkGroupID.x;          // spectrum row (the kz index)
    uint c = gl_WorkGroupID.y;          // cascade
    float tile = Cascade[c].x;
    float time = Timing.x;

    // Bit-reversed gather straight out of the spectrum: the permutation the in-place transform needs costs
    // nothing here because the load is a gather either way.
    uint lo = reversed(t), hi = reversed(t + @HALF@u);
    vec2 a0, a1, a2, a3, b0, b1, b2, b3;
    packedFields(c, lo, n, tile, time, a0, a1, a2, a3);
    packedFields(c, hi, n, tile, time, b0, b1, b2, b3);
    Line[t] = a0;                Line[t + @HALF@u] = b0;
    Line[@N@u + t] = a1;         Line[@N@u + t + @HALF@u] = b1;
    Line[2u * @N@u + t] = a2;    Line[2u * @N@u + t + @HALF@u] = b2;
    Line[3u * @N@u + t] = a3;    Line[3u * @N@u + t + @HALF@u] = b3;
    barrier();

    butterflies(t);

    // Row-major by spectrum row: Work[cascade][field][n * N + x].
    uint stride = @NN@u;
    uint rowBase = c * 4u * stride + n * @N@u;
    for (uint f = 0u; f < 4u; f++) {
        Work[f * stride + rowBase + t] = Line[f * @N@u + t];
        Work[f * stride + rowBase + t + @HALF@u] = Line[f * @N@u + t + @HALF@u];
    }
}
";

        /// <summary>
        /// Pass 2: one workgroup per (spatial column, cascade). Inverse-transforms along Z, then assembles the two
        /// output maps and advances the foam accumulator for its column.
        /// </summary>
        const string ColumnPassTemplate = @"#version 450
layout(local_size_x = @HALF@) in;
" + ParamsGlsl + @"
layout(std430, set = 0, binding = 1) buffer WorkBuf { vec2 Work[]; };
layout(std430, set = 0, binding = 2) buffer FoamBuf { float Foam[]; };
layout(set = 0, binding = 3, rgba16f) uniform writeonly image2DArray OceanMap;
" + CommonGlsl + @"
// One texel of both output maps, from the transformed column left in `Line`. Called twice per thread (the two
// halves of the column). Returns this texel's new foam value rather than writing it: the foam BUFFER is touched
// only by main, so that every buffer this kernel binds is first referenced in binding order (see the
// binding-order note on this class, and OceanFftShaderValidationTests, which fails the fast lane if that is broken).
float emit(uint c, uint cascades, uint px, uint pz, float lambda, float dt, float foamPrev,
           float foamGain, float foamBias, float foamDecay) {
    vec2 f0 = Line[pz];
    vec2 f1 = Line[@N@u + pz];
    vec2 f2 = Line[2u * @N@u + pz];
    vec2 f3 = Line[3u * @N@u + pz];

    // The spectrum is centred on k = 0, so the wave numbers run from -N/2 to N/2-1 while the transform assumes
    // 0..N-1. The half-grid shift that reconciles them is exactly a (-1)^(x+z) sign flip in the spatial domain,
    // which is cheaper here than shifting the spectrum would be.
    float s = ((px + pz) % 2u == 0u) ? 1.0 : -1.0;

    float height = f0.x * s;
    float dispX  = f0.y * s;
    float dispZ  = f1.x * s;
    float slopeX = f1.y * s;
    float slopeZ = f2.x * s;
    float dxdx   = f2.y * s;
    float dzdz   = f3.x * s;
    float dxdz   = f3.y * s;

    // Jacobian of the horizontal displacement. 1 is undeformed, above 1 is a stretched trough, and below 0 the
    // surface has folded back through itself - which is what a breaking crest is, and where foam comes from.
    float jxx = 1.0 + lambda * dxdx;
    float jzz = 1.0 + lambda * dzdz;
    float jxz = lambda * dxdz;
    float jacobian = jxx * jzz - jxz * jxz;

    // Foam ACCUMULATES: it decays exponentially and takes an injection wherever the surface is folding, so a
    // break leaves a trail behind it instead of blinking off with the crest that made it.
    float decayed = foamPrev * exp(-max(foamDecay, 0.0) * dt);
    float inject = max(0.0, foamBias - jacobian) * max(foamGain, 0.0);
    float foam = clamp(decayed + inject * dt, 0.0, 1.0);

    // ONE map array carries both halves: displacement in layers [0, cascades), derivatives in
    // [cascades, 2*cascades). One texture rather than two is not tidiness - it is what lets the water shaders
    // bind a single ocean texture ahead of everything else, which is the only arrangement whose per-stage Metal
    // slot numbering agrees with the resource layout's (see WaterRenderer's layout note).
    imageStore(OceanMap, ivec3(int(px), int(pz), int(c)),
               vec4(dispX * lambda, height, dispZ * lambda, 1.0));
    imageStore(OceanMap, ivec3(int(px), int(pz), int(c + cascades)),
               vec4(slopeX, slopeZ, foam, jacobian));
    return foam;
}

void main() {
    uint t = gl_LocalInvocationID.x;
    uint px = gl_WorkGroupID.x;         // spatial column
    uint c = gl_WorkGroupID.y;          // cascade

    // Params, then Work, then Foam: the buffers are first referenced here, in binding order.
    float lambda = Timing.z;
    float dt = max(Timing.y, 0.0);
    float foamGain = Foaming.x, foamBias = Foaming.y, foamDecay = Foaming.z;
    uint cascades = uint(Foaming.w + 0.5);

    uint stride = @NN@u;
    uint colBase = c * 4u * stride + px;
    uint lo = reversed(t), hi = reversed(t + @HALF@u);
    for (uint f = 0u; f < 4u; f++) {
        Line[f * @N@u + t] = Work[f * stride + colBase + lo * @N@u];
        Line[f * @N@u + t + @HALF@u] = Work[f * stride + colBase + hi * @N@u];
    }
    barrier();

    butterflies(t);

    // Each texel is owned by exactly one invocation across the whole dispatch, so this read-modify-write of the
    // foam accumulator has no cross-invocation hazard and needs no ordering of its own.
    uint fiLo = c * @NN@u + t * @N@u + px;
    uint fiHi = c * @NN@u + (t + @HALF@u) * @N@u + px;
    float prevLo = Foam[fiLo], prevHi = Foam[fiHi];
    Foam[fiLo] = emit(c, cascades, px, t, lambda, dt, prevLo, foamGain, foamBias, foamDecay);
    Foam[fiHi] = emit(c, cascades, px, t + @HALF@u, lambda, dt, prevHi, foamGain, foamBias, foamDecay);
}
";
    }
}
