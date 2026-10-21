using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Task 7g.3 step 0: the carried-lock release as it ships (P, its two <c>precise</c> markings included) and the barrier
/// form (B, every input and rounded intermediate through <c>temporalOrdered</c>, the division by three written as a
/// multiply by <c>fl(1/3)</c>, no <c>precise</c>), each compiled as its own program, on the same cases, one case per
/// fragment. The cases are built in C# and uploaded as single-float textures, so both programs read them bit for bit
/// and no compiler sees a constant. <see cref="Barrier"/> is B's C# twin, every step rounded to float32.
/// <see cref="Division"/> is P's twin with a correctly rounded division, reported only.
/// </summary>
internal sealed class TemporalLockReleaseProbe : IDisposable
{
    /// <summary>P: <c>temporalReleaseLock</c> as it ships, verbatim. A device-free fact holds it to the production
    /// text.</summary>
    internal const string PreciseHelperGlsl = @"float temporalReleaseLock(float value, float motionPixels, float reactive, float edgeMotion) {
    precise float motionRelease = clamp((motionPixels - LockMotionStartPixels)
        / (LockMotionEndPixels - LockMotionStartPixels), 0.0, 1.0);
    precise float result = value * ((1.0 - motionRelease) * (1.0 - clamp(reactive * LockReactiveRelease, 0.0, 1.0))
        * (1.0 - clamp(edgeMotion * LockEdgeRelease, 0.0, 1.0)));
    return result;
}";

    /// <summary>fl(1 / (LockMotionEndPixels - LockMotionStartPixels)), the float32 0x3EAAAAAB written exactly.</summary>
    internal const float MotionReleaseScale = 0.3333333432674407958984375f;

    /// <summary>B: the barrier form. <c>Sizes.x</c> is positive, so its sign bit is clear and every barrier ORs in
    /// zero, a value no compiler can see.</summary>
    internal const string BarrierHelperGlsl = @"
const float LockMotionReleaseScale = 0.3333333432674407958984375;
float temporalOrdered(float v) { return uintBitsToFloat(floatBitsToUint(v) | (floatBitsToUint(Sizes.x) >> 31u)); }
float temporalReleaseLockOrdered(float value, float motionPixels, float reactive, float edgeMotion) {
    float moved = temporalOrdered(temporalOrdered(motionPixels) - LockMotionStartPixels);
    float motionKeep = temporalOrdered(1.0 - clamp(temporalOrdered(moved * LockMotionReleaseScale), 0.0, 1.0));
    float reactiveKeep = temporalOrdered(1.0 - clamp(temporalOrdered(reactive) * LockReactiveRelease, 0.0, 1.0));
    float edgeKeep = temporalOrdered(1.0 - clamp(temporalOrdered(edgeMotion) * LockEdgeRelease, 0.0, 1.0));
    float keep = temporalOrdered(temporalOrdered(motionKeep * reactiveKeep) * edgeKeep);
    return temporalOrdered(temporalOrdered(value) * keep);
}
";

    const string CaseBindings = @"
layout(set=0,binding=0) uniform texture2D CaseValue;
layout(set=0,binding=1) uniform texture2D CaseMotion;
layout(set=0,binding=2) uniform texture2D CaseReactive;
layout(set=0,binding=3) uniform texture2D CaseEdge;
layout(set=0,binding=4) uniform sampler LinearClamp;
";

    // B alone reads the uniforms, so P's program binds only what it reads.
    const string ProbeBinding = @"
layout(set=0,binding=5) uniform Probe { vec4 Sizes; };
";

    static string EntryPoint(string helper) => @"
layout(location=0) out float oResult;
void main() {
    ivec2 f = ivec2(gl_FragCoord.xy);
    oResult = " + helper + @"(texelFetch(sampler2D(CaseValue, LinearClamp), f, 0).r,
        texelFetch(sampler2D(CaseMotion, LinearClamp), f, 0).r, texelFetch(sampler2D(CaseReactive, LinearClamp), f, 0).r,
        texelFetch(sampler2D(CaseEdge, LinearClamp), f, 0).r);
}
";

    /// <summary>P's program.</summary>
    internal static string PreciseSource => "#version 450\n" + CaseBindings + ShaderSources.TemporalResolveTuningGlsl
        + PreciseHelperGlsl + "\n" + EntryPoint("temporalReleaseLock");

    /// <summary>B's program.</summary>
    internal static string BarrierSource => "#version 450\n" + CaseBindings + ProbeBinding
        + ShaderSources.TemporalResolveTuningGlsl + BarrierHelperGlsl + EntryPoint("temporalReleaseLockOrdered");

    /// <summary>Every program the probe compiles, by name.</summary>
    internal static (string Name, string Source)[] Sources => [("P", PreciseSource), ("B", BarrierSource)];

    /// <summary>B in C#, every step a float32 operation, no fused evaluation.</summary>
    internal static float Barrier(float value, float motionPixels, float reactive, float edgeMotion)
    {
        float moved = motionPixels - TemporalResolveTuning.LockMotionStartPixels;
        float motionScaled = moved * MotionReleaseScale;
        float motionKeep = 1f - Clamp(motionScaled);
        float reactiveScaled = reactive * TemporalResolveTuning.LockReactiveRelease;
        float reactiveKeep = 1f - Clamp(reactiveScaled);
        float edgeScaled = edgeMotion * TemporalResolveTuning.LockEdgeRelease;
        float edgeKeep = 1f - Clamp(edgeScaled);
        float pair = motionKeep * reactiveKeep;
        float keep = pair * edgeKeep;
        return value * keep;
    }

    /// <summary>P in C# with the division correctly rounded, for the report only.</summary>
    internal static float Division(float value, float motionPixels, float reactive, float edgeMotion)
    {
        float moved = motionPixels - TemporalResolveTuning.LockMotionStartPixels;
        float span = TemporalResolveTuning.LockMotionEndPixels - TemporalResolveTuning.LockMotionStartPixels;
        float motionRelease = Clamp(moved / span);
        float reactiveKeep = 1f - Clamp(reactive * TemporalResolveTuning.LockReactiveRelease);
        float edgeKeep = 1f - Clamp(edgeMotion * TemporalResolveTuning.LockEdgeRelease);
        float pair = (1f - motionRelease) * reactiveKeep;
        float keep = pair * edgeKeep;
        return value * keep;
    }

    // GLSL's clamp(x, 0.0, 1.0), min(max(x, 0), 1).
    static float Clamp(float x) => MathF.Min(MathF.Max(x, 0f), 1f);

    /// <summary>The probe's cases, one per fragment, rows top to bottom.</summary>
    internal sealed record Cases(float[] Value, float[] Motion, float[] Reactive, float[] Edge)
    {
        internal int Count => Value.Length;
    }

    // Motion: a strided sweep of every binade in [1, 4], the ulps either side of both clamp thresholds, uniform and
    // log-uniform values. Reactive and edge: zero, the ulps either side of their upper thresholds, uniform and
    // log-uniform values. Value: the half-float grid on [0, 1], denormals excluded, 0 and 1 drawn often.
    internal static Cases Build(int count)
    {
        var value = new float[count];
        var motion = new float[count];
        var reactive = new float[count];
        var edge = new float[count];
        uint rampStart = BitConverter.SingleToUInt32Bits(1f), rampEnd = BitConverter.SingleToUInt32Bits(4f);
        int slots = Math.Max(1, (count + 3) / 4);
        uint stride = Math.Max(1u, (uint)((rampEnd - rampStart) / (ulong)slots));
        for (int i = 0; i < count; i++)
        {
            ulong h = Hash((ulong)i * 4 + 1), hr = Hash((ulong)i * 4 + 2), he = Hash((ulong)i * 4 + 3);
            ulong hv = Hash((ulong)i * 4 + 4);
            motion[i] = (i % 4) switch
            {
                0 => FromBits(Math.Min(rampEnd, rampStart + (uint)(i / 4) * stride + (uint)(h % stride))),
                1 => (h & 15) == 0 ? 0f : Ulps((h & 16) == 0 ? 1f : 4f, (int)((h >> 8) % 129) - 64),
                2 => Unit(h) * 8f,
                _ => LogUniform(h, -24, 8),
            };
            reactive[i] = Around(hr, 0.5f);
            edge[i] = Around(he, 1f);
            value[i] = HalfGrid(hv);
        }
        return new Cases(value, motion, reactive, edge);
    }

    // Zero or a tiny normal, the ulps either side of the upper threshold, uniform on [0, 1.5 threshold) and log-uniform
    // below 4.
    static float Around(ulong h, float threshold) => (h & 3) switch
    {
        0 => (h & 4) == 0 ? 0f : LogUniform(h >> 3, -24, -12),
        1 => Ulps(threshold, (int)((h >> 8) % 129) - 64),
        2 => Unit(h) * 1.5f * threshold,
        _ => LogUniform(h, -24, 2),
    };

    // A half float on [0, 1]: zero, every normal up to one, and 0 and 1 each a sixty-fourth of the time.
    static float HalfGrid(ulong h)
    {
        ulong pick = (h >> 32) % 64;
        if (pick == 0) return 0f;
        if (pick == 1) return 1f;
        const int Normals = 0x3C00 - 0x0400 + 1;
        int index = (int)(h % (Normals + 1));
        return index == 0 ? 0f : (float)BitConverter.Int16BitsToHalf((short)(0x0400 + index - 1));
    }

    static float LogUniform(ulong h, int lowExponent, int highExponent)
    {
        int exponent = lowExponent + (int)((h >> 23) % (ulong)(highExponent - lowExponent));
        return FromBits(((uint)(exponent + 127) << 23) | (uint)(h & 0x7FFFFF));
    }

    static float Ulps(float v, int k) => FromBits((uint)((int)BitConverter.SingleToUInt32Bits(v) + k));
    static float Unit(ulong h) => (h >> 40) * (1f / 16777216f);
    static float FromBits(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    static ulong Hash(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Which side of each clamp a case falls on in B's twin, and the value's ends: the branches the cases
    /// must meet.</summary>
    internal enum Branch
    {
        MotionBelow, MotionInside, MotionAbove, ReactiveBelow, ReactiveInside, ReactiveAbove, EdgeBelow, EdgeInside,
        EdgeAbove, ValueZero, ValueOne,
    }

    /// <summary>The count of cases on each <see cref="Branch"/>.</summary>
    internal static long[] Coverage(Cases cases)
    {
        var counts = new long[Enum.GetValues<Branch>().Length];
        for (int i = 0; i < cases.Count; i++)
        {
            float motionScaled = (cases.Motion[i] - TemporalResolveTuning.LockMotionStartPixels) * MotionReleaseScale;
            counts[(int)Side(motionScaled, Branch.MotionBelow)]++;
            counts[(int)Side(cases.Reactive[i] * TemporalResolveTuning.LockReactiveRelease, Branch.ReactiveBelow)]++;
            counts[(int)Side(cases.Edge[i] * TemporalResolveTuning.LockEdgeRelease, Branch.EdgeBelow)]++;
            if (cases.Value[i] == 0f) counts[(int)Branch.ValueZero]++;
            if (cases.Value[i] == 1f) counts[(int)Branch.ValueOne]++;
        }
        return counts;
    }

    static Branch Side(float scaled, Branch below) => scaled <= 0f ? below : scaled >= 1f ? below + 2 : below + 1;

    readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _preciseLayout, _barrierLayout;
    readonly Pass _precise, _barrier;
    readonly IGpuBuffer _uniforms;
    readonly IGpuTexture[] _inputs;
    internal IGpuDevice Device => _gpu.GpuDevice;
    internal readonly int Size;

    sealed record Pass(string Name, IGpuPipeline Pipeline, IGpuFramebuffer Framebuffer, IGpuTexture Target);

    /// <summary>A probe drawing <paramref name="size"/> squared cases.</summary>
    internal TemporalLockReleaseProbe(int size)
    {
        Size = size;
        IGpuResourceFactory f = Device.Factory;
        _uniforms = Own(f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer)));
        var elements = new List<GpuResourceLayoutElement>();
        foreach (string name in new[] { "CaseValue", "CaseMotion", "CaseReactive", "CaseEdge" })
            elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
        _preciseLayout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray())));
        elements.Add(new("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        _barrierLayout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray())));
        _inputs = Enumerable.Range(0, 4).Select(_ => Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)Size,
            (uint)Size, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled)))).ToArray();
        _precise = MakePass("P", PreciseSource, _preciseLayout);
        _barrier = MakePass("B", BarrierSource, _barrierLayout);
    }

    Pass MakePass(string name, string source, IGpuResourceLayout layout)
    {
        IGpuResourceFactory f = Device.Factory;
        IGpuTexture target = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)Size, (uint)Size,
            GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        IGpuFramebuffer framebuffer = Own(f.CreateFramebuffer(null, [target]));
        IGpuShaderSet shaders = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, source));
        IGpuPipeline pipeline = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = framebuffer.Outputs,
        }));
        return new Pass(name, pipeline, framebuffer, target);
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    /// <summary>P's and B's results, one per case, each target cleared to -1 first so an unwritten case shows.</summary>
    internal (float[] Precise, float[] Barrier) Run(Cases cases)
    {
        if (cases.Count != Size * Size) throw new ArgumentException("one case per fragment", nameof(cases));
        TemporalTextureIo.Upload(Device, _inputs[0], cases.Value);
        TemporalTextureIo.Upload(Device, _inputs[1], cases.Motion);
        TemporalTextureIo.Upload(Device, _inputs[2], cases.Reactive);
        TemporalTextureIo.Upload(Device, _inputs[3], cases.Edge);
        Device.UpdateBuffer(_uniforms, 0, new float[] { Size, Size, 0, 0 });
        var common = new List<IGpuBindableResource>(_inputs) { Device.LinearSampler };
        using IGpuResourceSet preciseSet = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_preciseLayout,
            common.ToArray()));
        common.Add(_uniforms);
        using IGpuResourceSet barrierSet = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_barrierLayout,
            common.ToArray()));
        Draw(_precise, preciseSet);
        Draw(_barrier, barrierSet);
        return (TemporalTextureIo.Read(Device, _precise.Target), TemporalTextureIo.Read(Device, _barrier.Target));
    }

    void Draw(Pass pass, IGpuResourceSet set)
    {
        using IGpuCommandList commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, nameof(TemporalLockReleaseProbe) + " " + pass.Name))
        {
            commands.SetFramebuffer(pass.Framebuffer);
            commands.ClearColorTarget(0, new Color(-1f, -1f, -1f, -1f));
            commands.SetPipeline(pass.Pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
        }
        Device.Submit(commands);
        Device.WaitForIdle();
    }

    public void Dispose()
    {
        Device.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _gpu.Dispose();
    }
}
