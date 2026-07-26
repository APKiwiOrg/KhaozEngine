namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GLSL 450 sources for the compute proof suite. Kept in one place so <see cref="ComputeShaderValidationTests"/>
    /// can cross-compile every one of them in the fast GPU-free lane while the <c>[GpuFact]</c> suites run them on a
    /// real device.
    /// </summary>
    /// <remarks>
    /// <see cref="FftStage"/> is deliberately a complete, working radix-2 Stockham FFT stage rather than a toy: it is
    /// the seed pattern for the FFT ocean program that consumes this compute seam, so the algorithm is validated on
    /// every backend before any ocean code exists.
    /// </remarks>
    internal static class ComputeShaders
    {
        /// <summary>Workgroup-local reduction: each workgroup sums <c>GroupSize</c> elements of <c>Src</c> into
        /// <c>Dst[gl_WorkGroupID.x]</c>. Run twice (elements -> per-group partials -> one total) to reduce an array.
        /// Unsigned integers so the expected value is exact, with no float-summation-order ambiguity.</summary>
        public const string Reduce = @"#version 450
layout(local_size_x = 256) in;

layout(set = 0, binding = 0) uniform Params { uint Count; uint Pad0; uint Pad1; uint Pad2; };
layout(std430, set = 0, binding = 1) buffer SrcBuf { uint Src[]; };
layout(std430, set = 0, binding = 2) buffer DstBuf { uint Dst[]; };

shared uint partial[256];

void main() {
    uint lid = gl_LocalInvocationID.x;
    uint gid = gl_GlobalInvocationID.x;
    partial[lid] = gid < Count ? Src[gid] : 0u;
    barrier();
    for (uint s = 128u; s > 0u; s >>= 1u) {
        if (lid < s) { partial[lid] += partial[lid + s]; }
        barrier();
    }
    if (lid == 0u) { Dst[gl_WorkGroupID.x] = partial[0]; }
}
";

        /// <summary>Writes a per-texel address pattern into a storage image: red = x, green = y, both encoded so the
        /// UNorm8 round-trip is EXACT (<c>x / 255.0</c> re-quantizes to <c>x</c>), which is what lets the readback
        /// assert every texel rather than a tolerance. The image is declared <c>writeonly</c>, so no backend needs
        /// typed UAV LOAD support for this format.</summary>
        public const string WriteImage = @"#version 450
layout(local_size_x = 8, local_size_y = 8) in;
layout(set = 0, binding = 0, rgba8) uniform writeonly image2D Dst;
layout(set = 0, binding = 1) uniform Params { uint Size; uint Pad0; uint Pad1; uint Pad2; };

void main() {
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (p.x >= int(Size) || p.y >= int(Size)) { return; }
    imageStore(Dst, p, vec4(float(p.x) / 255.0, float(p.y) / 255.0, 0.0, 1.0));
}
";

        /// <summary>Fullscreen triangle. Pairs with <see cref="SampleFrag"/>; the fragment shader derives its UV from
        /// <c>gl_FragCoord</c> rather than a varying, so the pass is immune to the backends' clip-space Y
        /// disagreement (<c>gl_FragCoord</c> is top-left origin on Metal, Vulkan and Direct3D11 alike).</summary>
        public const string FullscreenVert = @"#version 450
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
";

        /// <summary>Samples the compute-written storage image 1:1 into a same-sized render target.</summary>
        public const string SampleFrag = @"#version 450
layout(set = 0, binding = 0) uniform texture2D Src;
layout(set = 0, binding = 1) uniform sampler Samp;
layout(set = 0, binding = 2) uniform Params { uint Size; uint Pad0; uint Pad1; uint Pad2; };
layout(location = 0) out vec4 oColor;

void main() {
    oColor = texture(sampler2D(Src, Samp), gl_FragCoord.xy / float(Size));
}
";

        /// <summary>One radix-2 Stockham autosort FFT stage over a complex array, ping-ponging <c>Src</c> to
        /// <c>Dst</c>. Stockham needs no bit-reversal pass and reads/writes different buffers, which is exactly what
        /// a GPU wants.
        ///
        /// Per stage, with <c>Mh</c> the half-span (1, 2, 4, ... N/2) and thread <c>t</c> in <c>[0, N/2)</c>:
        /// <c>k = t / Mh</c>, <c>j = t % Mh</c>, and
        /// <c>dst[k*2Mh + j] = a + w*b</c>, <c>dst[k*2Mh + j + Mh] = a - w*b</c>
        /// where <c>a = src[t]</c>, <c>b = src[t + N/2]</c>, <c>w = exp(Sign * 2*pi*i * j / (2*Mh))</c>.
        ///
        /// The same kernel does rows and columns: <c>Stride</c> steps between elements of one transform (1 for a row,
        /// N for a column) and <c>LineStride</c> steps between transforms (N for a row, 1 for a column), so the 2D
        /// transform is a row sweep followed by a column sweep with no transpose pass.
        ///
        /// <c>Scale</c> multiplies both outputs; it is 1 except on the final stage of an inverse axis sweep, where it
        /// carries the 1/N normalization so no separate normalize dispatch is needed.</summary>
        public const string FftStage = @"#version 450
layout(local_size_x = 64) in;

struct Complex { vec2 v; };

layout(set = 0, binding = 0) uniform Params {
    uint N;           // transform length, a power of two
    uint Mh;          // half-span of this stage: 1, 2, 4, ... N/2
    uint Stride;      // element step within one transform
    uint LineStride;  // step between transforms
    float Sign;       // -1 forward, +1 inverse (sign of the twiddle exponent)
    float Scale;      // output scale (1, or 1/N on the last inverse stage of an axis)
};
layout(std430, set = 0, binding = 1) buffer SrcBuf { Complex Src[]; };
layout(std430, set = 0, binding = 2) buffer DstBuf { Complex Dst[]; };

vec2 cmul(vec2 a, vec2 b) { return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x); }

void main() {
    uint g = gl_GlobalInvocationID.x;
    uint half_n = N >> 1u;
    uint line = g / half_n;         // which row (or column) this thread works on
    uint t = g - line * half_n;     // butterfly index within that transform
    if (line >= N) { return; }

    uint k = t / Mh;
    uint j = t - k * Mh;

    uint base = line * LineStride;
    vec2 a = Src[base + t * Stride].v;
    vec2 b = Src[base + (t + half_n) * Stride].v;

    float ang = Sign * 6.283185307179586 * float(j) / float(2u * Mh);
    vec2 w = vec2(cos(ang), sin(ang));
    vec2 wb = cmul(w, b);

    uint lo = (k * 2u * Mh) + j;
    Dst[base + lo * Stride].v = (a + wb) * Scale;
    Dst[base + (lo + Mh) * Stride].v = (a - wb) * Scale;
}
";
    }
}
