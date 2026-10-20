using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// One fragment program that runs frozen D (<see cref="TemporalVisibilityFrozenD"/>) and the production decision on
/// the same case, one case per fragment, and an independent classifier that says which inputs and which predicted
/// paths the case exercised. Each fragment builds its case from a hash PRNG against full-size textures
/// (<see cref="TemporalVisibilityEquivalenceInputs"/>). It writes three targets: an RGBA8 of flag bits
/// (<see cref="Flag"/>), D's signed change and the production signed change.
/// </summary>
internal sealed class TemporalVisibilityEquivalenceProbe : IDisposable
{
    /// <summary>The flag bits, numbered across the four RGBA8 channels, red's bit 0 first.</summary>
    internal enum Flag
    {
        DValid, DRelease, NewComputed, NewRelease, DecisionMismatch, ValidityMismatch, ChangeMismatch, Written,
        PredictedFast, PredictedSlowEdge, PredictedSlowDeviation, PredictedSlowBorder, PredictedRangeSkip,
        RangeSkipNearMarginBelow, RangeSkipNearMarginAbove, RangeSkipMeetsRelease,
        EdgeCentre, CornerCentre, NearIntegerFraction, NonFinite, Background, CurrentToleranceExact,
        CurrentToleranceUlp, PreviousToleranceExact,
        PreviousToleranceUlp, NearThresholdAbove, NearThresholdBelow, ReportedFast, ReportedSlow, ReportedRangeSkip,
        ReportedPathMismatch, EmptyHistory,
    }

    /// <summary>
    /// THE PRODUCTION SIDE: the shipped decision, <c>temporalVisibilityReleases</c>, and the path bits it reports. A
    /// range skip leaves <c>computed</c> false where D computed a change, and only there may the validities differ.
    /// </summary>
    internal const string ProductionDecisionGlsl = @"
const uint ReportedFast = 1u, ReportedSlow = 2u, ReportedRangeSkip = 4u;
bool productionReleases(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    out bool computed, out float change, out uint path) {
    change = 0.0;
    uint shipped = 0u;
    bool release = temporalVisibilityReleases(uv, centre, last, previousUv, expectedDepth, change, shipped);
    computed = (shipped & VisibilityChangeComputed) != 0u;
    path = ((shipped & VisibilityFastPath) != 0u ? ReportedFast : 0u)
        | ((shipped & VisibilitySlowPath) != 0u ? ReportedSlow : 0u)
        | ((shipped & VisibilityRangeSkip) != 0u ? ReportedRangeSkip : 0u);
    return release;
}
";

    const string Header = @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform texture2D PrevDepth;
layout(set=0,binding=3) uniform texture2D KeyShadowVisibility;
layout(set=0,binding=4) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0,binding=5) uniform sampler LinearClamp;
layout(set=0,binding=6) uniform Probe { vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter; vec4 Seed; };
";

    // The case: a centre texel, the pixel's UV, a previous UV that puts the centre's history position at a chosen
    // texel plus a chosen fraction, and an expected depth related to the stored depths there.
    const string CaseGlsl = @"
uint caseHash(uint v) {
    v ^= v >> 16; v *= 0x7feb352du; v ^= v >> 15; v *= 0x846ca68bu; v ^= v >> 16;
    return v;
}
uint caseNext(inout uint s) { s = caseHash(s + 0x9e3779b9u); return s; }
float caseUnit(inout uint s) { return float(caseNext(s) >> 8) * (1.0 / 16777216.0); }
int caseInt(inout uint s, int n) { return int(caseNext(s) % uint(n)); }
// k ulps away from a positive finite value, the value itself otherwise.
float caseUlps(float v, int k) {
    return v > 0.0 && v < 3.0e38 ? uintBitsToFloat(uint(int(floatBitsToUint(v)) + k)) : v;
}
int caseEdge(inout uint s, int last) {
    int r = caseInt(s, 5);
    return r == 0 ? 0 : r == 1 ? 1 : r == 2 ? last - 1 : r == 3 ? last : caseInt(s, last + 1);
}
struct VisibilityCase { vec2 uv; ivec2 centre; vec2 previousUv; float expectedDepth; };
VisibilityCase buildCase(ivec2 fragment) {
    uint s = caseHash(uint(fragment.x) * 0x8da6b343u ^ uint(fragment.y) * 0xd8163841u
        ^ (uint(Seed.x) + 1u) * 0xcb1ab31fu);
    vec2 size = Sizes.xy;
    ivec2 last = ivec2(size) - 1;
    VisibilityCase c;
    float mode = caseUnit(s);
    bool designed = mode < 0.45;
    vec2 inTexel = vec2(caseUnit(s), caseUnit(s)) * 0.98 - 0.49;
    if (designed) {
        ivec2 tiles = ivec2(size) / 8;
        c.centre = ivec2(caseInt(s, tiles.x), caseInt(s, tiles.y)) * 8 + 3;
    } else if (mode < 0.6) {
        // Edges and corners, and centres one texel in, whose history block can leave the texture.
        c.centre = ivec2(caseEdge(s, last.x), caseEdge(s, last.y));
        float force = caseUnit(s);
        if (force < 0.35) c.centre.x = caseUnit(s) < 0.5 ? 0 : last.x;
        else if (force < 0.7) c.centre.y = caseUnit(s) < 0.5 ? 0 : last.y;
    } else {
        c.centre = ivec2(caseInt(s, last.x + 1), caseInt(s, last.y + 1));
    }
    c.uv = (vec2(c.centre) + 0.5 - Jitter.xy + inTexel) / size;
    if (!designed && mode >= 0.6 && mode < 0.85) {
        // A display pixel and the texel its jittered sample lands in, as temporalPixelSite finds them.
        vec2 pixel = floor(vec2(caseUnit(s), caseUnit(s)) * Sizes.zw);
        c.uv = (pixel + 0.5) / Sizes.zw;
        c.centre = clamp(ivec2(floor(c.uv * size + Jitter.xy)), ivec2(0), last);
    }
    float motionKind = caseUnit(s);
    vec2 shift = designed ? (motionKind < 0.5 ? vec2(0.0) : vec2(caseInt(s, 3) - 1, caseInt(s, 3) - 1))
        : motionKind < 0.6 ? vec2(caseInt(s, 7) - 3, caseInt(s, 7) - 3)
        : floor((vec2(caseUnit(s), caseUnit(s)) - 0.5) * 40.0);
    vec2 target = vec2(c.centre) + shift;
    if (mode >= 0.45 && mode < 0.6 && caseUnit(s) < 0.5)
        target = vec2(caseEdge(s, last.x), caseEdge(s, last.y));
    float fractionKind = caseUnit(s);
    vec2 fraction = fractionKind < 0.25 ? vec2(0.0)
        : fractionKind < 0.35 ? (vec2(caseUnit(s), caseUnit(s)) - 0.5) * 2.0e-3
        : vec2(caseUnit(s), caseUnit(s));
    // The centre's history position is previousUv * S + (centre + 0.5 - J) - uv * S + Jp - 0.5, so this previous UV
    // lands it at target + fraction, up to rounding.
    vec2 offsetInTexel = c.uv * size - (vec2(c.centre) + 0.5 - Jitter.xy);
    c.previousUv = (target + fraction + 0.5 + offsetInTexel - PreviousJitter.xy) / size;
    if (fractionKind < 0.35 && caseUnit(s) < 0.6)
        c.previousUv = vec2(caseUlps(c.previousUv.x, caseInt(s, 7) - 3), caseUlps(c.previousUv.y, caseInt(s, 7) - 3));
    c.previousUv = clamp(c.previousUv, vec2(0.0), vec2(1.0));

    ivec2 base = clamp(ivec2(floor(target + fraction)), ivec2(0), last);
    float corner = texelFetch(sampler2D(PrevDepth, LinearClamp), base, 0).r;
    float site = texelFetch(sampler2D(PrevDepth, LinearClamp), clamp(base / 8 * 8 + 3, ivec2(0), last), 0).r;
    float r = caseUnit(s);
    float e;
    if (r < 0.30) e = corner;
    else if (r < 0.45) e = site;
    else if (r < 0.60) e = caseUlps(corner, caseInt(s, 7) - 3);
    else if (r < 0.72) e = caseUlps(site / 1.02, caseInt(s, 5) - 2);
    else if (r < 0.77) e = caseUlps(site * 1.02, caseInt(s, 5) - 2);
    else if (r < 0.85) e = 0.05 * exp2(caseUnit(s) * 12.0);
    else if (r < 0.90) {
        int special = caseInt(s, 7);
        e = special == 0 ? uintBitsToFloat(0x7fc00000u) : special == 1 ? uintBitsToFloat(0x7f800000u)
            : special == 2 ? uintBitsToFloat(0xff800000u) : special == 3 ? 0.0 : special == 4 ? -1.0
            : special == 5 ? 1.0e30 : 1.0e-6;
    } else e = texelFetch(sampler2D(PrevDepth, LinearClamp),
        clamp(base + ivec2(caseInt(s, 7) - 3, caseInt(s, 7) - 3), ivec2(0), last), 0).r;
    c.expectedDepth = e;
    return c;
}
";

    // Independent of both sides: which inputs the case met, and which path the cost cut's preconditions (separable
    // consecutive bases, a 4x4 block inside the texture) and its R4 bound predict.
    const string ClassifierGlsl = @"
bool classFinite(float v) { return (floatBitsToUint(v) & 0x7f800000u) != 0x7f800000u; }
uint classify(VisibilityCase c, bool dValid, float dChange, bool dRelease) {
    uint bits = 0u;
    ivec2 last = ivec2(Sizes.xy) - 1;
    bool edgeX = c.centre.x == 0 || c.centre.x == last.x, edgeY = c.centre.y == 0 || c.centre.y == last.y;
    if (edgeX || edgeY) bits |= 1u << 16;
    if (edgeX && edgeY) bits |= 1u << 17;
    float centreDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), c.centre, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), c.centre, 0).r);
    float e = c.expectedDepth;
    bool references = classFinite(centreDepth) && classFinite(e);
    if (!references) bits |= 1u << 19;
    float currentTolerance = VisibilityDepthTolerance * max(centreDepth, 1.0e-5);
    float previousTolerance = VisibilityDepthTolerance * max(e, 1.0e-5);
    float currentSum = 0.0, currentCount = 0.0, previousAccepted = 0.0;
    int baseX[3], baseY[3];
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(c.centre + ivec2(x, y), ivec2(0), last);
            vec2 motion = texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg;
            if (abs(motion.x) > MotionSentinel) bits |= 1u << 20;
            float depth = temporalViewDepth(motion, texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r);
            vec2 historyUv = c.previousUv + (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy - c.uv + PreviousJitter.xy / Sizes.xy;
            vec2 position = historyUv * Sizes.xy - 0.5;
            ivec2 base = ivec2(floor(position));
            if (y == 0) baseX[x + 1] = base.x;
            if (x == 0) baseY[y + 1] = base.y;
            vec2 fraction = position - vec2(base);
            if (x == 0 && y == 0 && any(lessThan(min(fraction, 1.0 - fraction), vec2(1.0e-3)))) bits |= 1u << 18;
            if (!classFinite(depth)) { bits |= 1u << 19; continue; }
            if (!references) continue;
            float apart = abs(depth - centreDepth);
            if (apart == currentTolerance) bits |= 1u << 21;
            float up = centreDepth + currentTolerance, down = centreDepth - currentTolerance;
            if (depth == caseUlps(up, 1) || depth == caseUlps(up, -1) || depth == caseUlps(down, 1)
                || depth == caseUlps(down, -1)) bits |= 1u << 22;
            if (!(apart <= currentTolerance)) continue;
            currentSum += texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), at, 0).r;
            currentCount += 1.0;
            for (int j = 0; j < 2; j++) {
                for (int i = 0; i < 2; i++) {
                    float stored = texelFetch(sampler2D(PrevDepth, LinearClamp),
                        clamp(base + ivec2(i, j), ivec2(0), last), 0).r;
                    if (!classFinite(stored)) { bits |= 1u << 19; continue; }
                    if (abs(stored - e) <= previousTolerance) previousAccepted += 1.0;
                    if (abs(stored - e) == previousTolerance) bits |= 1u << 23;
                    float pUp = e + previousTolerance, pDown = e - previousTolerance;
                    if (stored == caseUlps(pUp, 1) || stored == caseUlps(pUp, -1) || stored == caseUlps(pDown, 1)
                        || stored == caseUlps(pDown, -1)) bits |= 1u << 24;
                }
            }
        }
    }
    if (references && currentCount > 0.0 && previousAccepted == 0.0) bits |= 1u << 31;
    bool consecutive = baseX[0] == baseX[1] - 1 && baseX[2] == baseX[1] + 1
        && baseY[0] == baseY[1] - 1 && baseY[2] == baseY[1] + 1;
    bool inside = baseX[1] - 1 >= 0 && baseX[1] + 2 <= last.x && baseY[1] - 1 >= 0 && baseY[1] + 2 <= last.y;
    bool fast = !(edgeX || edgeY) && consecutive && inside;
    if (edgeX || edgeY) bits |= 1u << 9;
    else if (!consecutive) bits |= 1u << 10;
    else if (!inside) bits |= 1u << 11;
    else bits |= 1u << 8;
    if (fast && references && currentCount > 0.0) {
        float lowest = 2.0, highest = -1.0;
        for (int j = -1; j <= 2; j++)
            for (int i = -1; i <= 2; i++) {
                float v = texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp),
                    ivec2(baseX[1] + i, baseY[1] + j), 0).r;
                lowest = min(lowest, v);
                highest = max(highest, v);
            }
        // The current mean's distance from each end of the block's range, times the count, against the margin times
        // the count: the same bound as the mean's own, which the shipped skip takes this way.
        float bound = max(currentSum - lowest * currentCount, highest * currentCount - currentSum);
        const float Margin = VisibilityReleaseThreshold - 1.220703125e-4;
        bool skip = bound <= Margin * currentCount;
        if (skip) bits |= 1u << 12;
        if (abs(bound - Margin * currentCount) <= 4.8828125e-4 * currentCount) bits |= skip ? 1u << 13 : 1u << 14;
        if (skip && dRelease) bits |= 1u << 15;
    }
    if (dValid && abs(abs(dChange) - VisibilityReleaseThreshold) <= 2.384185791015625e-7)
        bits |= abs(dChange) > VisibilityReleaseThreshold ? 1u << 25 : 1u << 26;
    return bits;
}
";

    const string Main = @"
layout(location=0) out vec4 oFlags;
layout(location=1) out float oDChange;
layout(location=2) out float oNewChange;
void main() {
    VisibilityCase c = buildCase(ivec2(gl_FragCoord.xy));
    ivec2 last = ivec2(Sizes.xy) - 1;
    float dChange = 0.0;
    bool dValid = frozenDVisibilityChange(c.uv, c.centre, last, c.previousUv, c.expectedDepth, dChange);
    bool dRelease = dValid && abs(dChange) > VisibilityReleaseThreshold;
    bool computed;
    float newChange;
    uint path;
    bool newRelease = productionReleases(c.uv, c.centre, last, c.previousUv, c.expectedDepth, computed, newChange,
        path);
    uint bits = classify(c, dValid, dChange, dRelease) | (1u << 7);
    if (dValid) bits |= 1u;
    if (dRelease) bits |= 2u;
    if (computed) bits |= 4u;
    if (newRelease) bits |= 8u;
    if (dRelease != newRelease) bits |= 16u;
    if ((computed && !dValid) || (dValid && !computed && (path & ReportedRangeSkip) == 0u)) bits |= 32u;
    if (computed && dValid && floatBitsToUint(dChange) != floatBitsToUint(newChange)) bits |= 64u;
    if ((path & ReportedFast) != 0u) bits |= 1u << 27;
    if ((path & ReportedSlow) != 0u) bits |= 1u << 28;
    if ((path & ReportedRangeSkip) != 0u) bits |= 1u << 29;
    // The reported path must be the predicted one, case for case: fast or slow, and the range skip.
    bool predictedFast = (bits & (1u << 8)) != 0u, predictedSkip = (bits & (1u << 12)) != 0u;
    bool reportedFast = (path & ReportedFast) != 0u, reportedSlow = (path & ReportedSlow) != 0u;
    if (reportedFast != predictedFast || reportedSlow == predictedFast
        || ((path & ReportedRangeSkip) != 0u) != predictedSkip) bits |= 1u << 30;
    oFlags = vec4(uvec4(bits & 255u, (bits >> 8) & 255u, (bits >> 16) & 255u, bits >> 24)) / 255.0;
    oDChange = dChange;
    oNewChange = computed ? newChange : 0.0;
}
";

    readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _layout;
    readonly IGpuPipeline _pipeline;
    readonly IGpuFramebuffer _framebuffer;
    readonly IGpuBuffer _uniforms;
    readonly IGpuTexture _flags, _dChange, _newChange;
    internal IGpuDevice Device => _gpu.GpuDevice;
    internal readonly int Size;

    internal static string FragmentSource => "#version 450\n" + Header + ShaderSources.TemporalCommonGlsl
        + ShaderSources.TemporalResolveTuningGlsl + ShaderSources.TemporalVisibilityGlsl
        + TemporalVisibilityFrozenD.Renamed("frozenD") + ProductionDecisionGlsl + CaseGlsl + ClassifierGlsl + Main;

    /// <summary>A probe drawing <paramref name="hardwareSize"/> squared cases a draw, or
    /// <paramref name="softwareSize"/> squared on a software adapter.</summary>
    internal TemporalVisibilityEquivalenceProbe(int hardwareSize, int softwareSize)
    {
        int size = Size = Device.Diagnostics.SoftwareAdapter == true ? softwareSize : hardwareSize;
        IGpuResourceFactory f = Device.Factory;
        _flags = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)size, (uint)size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        _dChange = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)size, (uint)size, GpuPixelFormat.R32Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        _newChange = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)size, (uint)size, GpuPixelFormat.R32Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        _framebuffer = Own(f.CreateFramebuffer(null, _flags, _dChange, _newChange));
        _uniforms = Own(f.CreateBuffer(new GpuBufferDescription(80, GpuBufferUsage.UniformBuffer)));
        var elements = new List<GpuResourceLayoutElement>();
        foreach (string name in new[] { "SceneDepth", "MotionTex", "PrevDepth", "KeyShadowVisibility", "PreviousKeyShadowVisibility" })
            elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
        elements.Add(new("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        _layout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray())));
        IGpuShaderSet shaders = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, FragmentSource));
        _pipeline = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend,
                GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = _framebuffer.Outputs,
        }));
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    /// <summary>The textures of one internal size, uploaded once for every draw at that size.</summary>
    internal sealed class Textures(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs) : IDisposable
    {
        internal readonly TemporalVisibilityEquivalenceInputs Inputs = inputs;
        internal readonly IGpuTexture Depth = Upload(device, inputs, GpuPixelFormat.R32Float, inputs.Depth);
        internal readonly IGpuTexture Motion = Upload(device, inputs, GpuPixelFormat.R16G16Float, inputs.Motion);
        internal readonly IGpuTexture PreviousDepth = Upload(device, inputs, GpuPixelFormat.R32Float, inputs.PreviousDepth);
        internal readonly IGpuTexture Current = Upload(device, inputs, inputs.Current);
        internal readonly IGpuTexture Previous = Upload(device, inputs, inputs.Previous);

        static IGpuTexture Create(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, GpuPixelFormat format)
            => device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)inputs.Width, (uint)inputs.Height,
                format, GpuTextureUsage.Sampled));

        static IGpuTexture Upload(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, GpuPixelFormat format,
            float[] values)
        {
            IGpuTexture texture = Create(device, inputs, format);
            TemporalTextureIo.Upload(device, texture, values);
            return texture;
        }

        static IGpuTexture Upload(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, byte[] bytes)
        {
            IGpuTexture texture = Create(device, inputs, GpuPixelFormat.R8UNorm);
            device.UpdateTexture(texture, bytes, 0, 0, (uint)inputs.Width, (uint)inputs.Height);
            return texture;
        }

        public void Dispose()
        {
            Depth.Dispose();
            Motion.Dispose();
            PreviousDepth.Dispose();
            Current.Dispose();
            Previous.Dispose();
        }
    }

    /// <summary>One draw: every fragment of the target one case, at a display size, a jitter pair and a seed. Returns
    /// the flag words and both changes, one per fragment, rows top to bottom.</summary>
    internal (uint[] Flags, float[] DChange, float[] NewChange) Run(Textures textures, Vector2 display, Vector2 jitter,
        Vector2 previousJitter, int seed)
    {
        float[] uniforms = [0, 0, 1, 0, textures.Inputs.Width, textures.Inputs.Height, display.X, display.Y,
            jitter.X, jitter.Y, 1, 1, previousJitter.X, previousJitter.Y, 1, 0, seed, 0, 0, 0];
        Device.UpdateBuffer(_uniforms, 0, uniforms);
        using IGpuResourceSet set = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
            textures.Depth, textures.Motion, textures.PreviousDepth, textures.Current, textures.Previous,
            Device.LinearSampler, _uniforms));
        using (IGpuCommandList commands = Device.Factory.CreateCommandList())
        {
            using (GpuRecording.Open(Device, commands, nameof(TemporalVisibilityEquivalenceProbe)))
            {
                commands.SetFramebuffer(_framebuffer);
                commands.SetPipeline(_pipeline);
                commands.SetGraphicsResourceSet(0, set);
                commands.Draw(3);
            }
            Device.Submit(commands);
            Device.WaitForIdle();
        }
        return (ReadFlags(), TemporalTextureIo.Read(Device, _dChange), TemporalTextureIo.Read(Device, _newChange));
    }

    uint[] ReadFlags()
    {
        using IGpuTexture staging = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)Size, (uint)Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
        using (IGpuCommandList commands = Device.Factory.CreateCommandList())
        {
            using (GpuRecording.Open(Device, commands, nameof(TemporalVisibilityEquivalenceProbe)))
                commands.CopyTexture(_flags, staging);
            Device.Submit(commands);
            Device.WaitForIdle();
        }
        var flags = new uint[Size * Size];
        MappedData map = Device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < Size; y++)
            {
                IntPtr row = map.Data + (int)(y * map.RowPitch);
                for (int x = 0; x < Size; x++) flags[y * Size + x] = (uint)Marshal.ReadInt32(row, x * 4);
            }
        }
        finally
        {
            Device.Unmap(staging);
        }
        return flags;
    }

    public void Dispose()
    {
        Device.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _gpu.Dispose();
    }
}
