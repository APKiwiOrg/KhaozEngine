using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Gather and Load must return the same bits at every interior 2x2 footprint, including those touching each texture
/// edge, at the real internal sizes, except that stored-depth NaNs need only remain NaN. NVIDIA gathers canonicalize
/// their bits. The consumer rejects every NaN by exponent bits before accepting history, so payload and sign cannot
/// affect its decision. Separate programs store all four components as integer bits encoded in RGBA8,
/// so neither shared compiler expressions nor a float render target can hide a rounding or NaN bit difference.
/// R8 inputs cover every UNORM value in every component. R32F inputs include distinct finite values, signed zeros,
/// infinities and NaNs of both signs with quiet and signalling payloads. Every non-NaN depth and every non-depth
/// value stays bit-exact. No numeric tolerance is admitted.
/// </summary>
public sealed class TemporalVisibilityGatherGpuTests(ITestOutputHelper output)
{
    public static TheoryData<int, int, bool> Cases => new()
    {
        { 2560, 1440, false }, { 2560, 1440, true },
        { 1707, 960, false }, { 1707, 960, true },
        { 3342, 2160, false }, { 3342, 2160, true },
    };

    // The low texel of each component, in texture coordinates with row 1 at higher V. This order is independently
    // pinned by the asymmetric Task 2 quad: x (0,1), y (1,1), z (1,0), w (0,0).
    static readonly (int X, int Y)[] Offsets = [(0, 1), (1, 1), (1, 0), (0, 0)];

    internal static string Program(bool gather) => @"#version 450
layout(set=0,binding=0) uniform texture2D Source;
layout(set=0,binding=1) uniform sampler LinearClamp;
layout(location=0) out vec4 oX;
layout(location=1) out vec4 oY;
layout(location=2) out vec4 oZ;
layout(location=3) out vec4 oW;
vec4 bytes(uint bits) {
    return vec4(float(bits & 255u), float((bits >> 8) & 255u), float((bits >> 16) & 255u),
        float(bits >> 24)) / 255.0;
}
void main() {
    ivec2 at = ivec2(gl_FragCoord.xy);
    vec4 values = " + (gather
        ? "textureGather(sampler2D(Source, LinearClamp), vec2(at + ivec2(1)) / vec2(textureSize(sampler2D(Source, LinearClamp), 0)), 0)"
        : @"vec4(texelFetch(sampler2D(Source, LinearClamp), at + ivec2(0, 1), 0).r,
        texelFetch(sampler2D(Source, LinearClamp), at + ivec2(1, 1), 0).r,
        texelFetch(sampler2D(Source, LinearClamp), at + ivec2(1, 0), 0).r,
        texelFetch(sampler2D(Source, LinearClamp), at, 0).r)") + @";
    uvec4 bits = floatBitsToUint(values);
    oX = bytes(bits.x); oY = bytes(bits.y); oZ = bytes(bits.z); oW = bytes(bits.w);
}
";

    [Fact]
    public void BothOracleProgramsCompileForEveryBackend()
    {
        ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, Program(true), "visibility gather bits");
        ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, Program(false), "visibility load bits");
    }

    [GpuTheory]
    [MemberData(nameof(Cases))]
    public void EveryGatherComponentEqualsLoadBits(int width, int height, bool depth)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        GpuPixelFormat format = depth ? GpuPixelFormat.R32Float : GpuPixelFormat.R8UNorm;
        byte[] input = Input(width, height, depth);
        using IGpuTexture source = device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width,
            (uint)height, format, GpuTextureUsage.Sampled));
        device.UpdateTexture(source, input, 0, 0, (uint)width, (uint)height);
        byte[][] gathered = Render(device, source, Program(true));
        byte[][] loaded = Render(device, source, Program(false));
        var misses = new long[4];
        var seen = new bool[4, 256];
        var examples = new List<string>();
        long edgeComponents = 0, nan = 0, nanBitChanges = 0, positiveInfinity = 0, negativeInfinity = 0;
        for (int c = 0; c < 4; c++)
        {
            Assert.Equal((width - 1) * (height - 1) * 4, loaded[c].Length);
            Assert.Equal(loaded[c].Length, gathered[c].Length);
            for (int y = 0; y < height - 1; y++)
                for (int x = 0; x < width - 1; x++)
                {
                    int i = (y * (width - 1) + x) * 4;
                    uint g = Bits(gathered[c], i), l = Bits(loaded[c], i);
                    int texel = (y + Offsets[c].Y) * width + x + Offsets[c].X;
                    if (x == 0 || y == 0 || x == width - 2 || y == height - 2) edgeComponents++;
                    if (depth)
                    {
                        uint expected = Bits(input, texel * 4);
                        if (l != expected) Assert.Fail($"Load component {c} at ({x},{y}) changed raw R32F bits "
                            + $"0x{expected:x8} to 0x{l:x8}");
                        if ((l & 0x7fffffff) > 0x7f800000)
                        {
                            nan++;
                            if ((g & 0x7fffffff) > 0x7f800000)
                            {
                                if (g != l) nanBitChanges++;
                                continue;
                            }
                        }
                        else if (l == 0x7f800000) positiveInfinity++;
                        else if (l == 0xff800000) negativeInfinity++;
                    }
                    else
                    {
                        float value = BitConverter.UInt32BitsToSingle(l);
                        if (!float.IsFinite(value) || value < 0 || value > 1
                            || input[texel] != (int)MathF.Round(value * 255))
                            Assert.Fail($"Load component {c} at ({x},{y}) did not return R8 {input[texel]}");
                        seen[c, input[texel]] = true;
                    }
                    if (g == l) continue;
                    misses[c]++;
                    if (examples.Count < 16)
                        examples.Add($"component {"xyzw"[c]} origin ({x},{y}), gather 0x{g:x8}, Load 0x{l:x8}");
                }
        }
        output.WriteLine($"{device.Backend} on {device.Capabilities.DeviceName}, {width}x{height} {format}: "
            + $"{4L * (width - 1) * (height - 1)} component comparisons, mismatches x/y/z/w "
            + $"{string.Join("/", misses)}, {edgeComponents} edge components, NaN {nan}, +inf {positiveInfinity}, "
            + $"-inf {negativeInfinity}, NaN-to-NaN bit changes {nanBitChanges}");
        foreach (string example in examples) output.WriteLine(example);
        Assert.True(edgeComponents > 0);
        if (depth) Assert.True(nan > 0 && positiveInfinity > 0 && negativeInfinity > 0);
        else
            for (int c = 0; c < 4; c++)
                for (int value = 0; value < 256; value++)
                    Assert.True(seen[c, value], $"R8 {value} never read in component {c}");
        Assert.True(misses.Sum() == 0, (depth
            ? "Gather must preserve Load NaNs as NaN and every other depth bit for bit. "
            : "Gather must equal Load bit for bit. ") + string.Join("; ", examples));
    }

    static uint Bits(byte[] bytes, int at) => BitConverter.ToUInt32(bytes, at);

    static byte[] Input(int width, int height, bool depth)
    {
        if (!depth)
        {
            var values = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) values[y * width + x] = (byte)(x + 37 * y);
            return values;
        }
        uint[] special = [0, 0x80000000, 0x7f800000, 0xff800000, 0x7fc00000, 0xffc12345, 0x7f800001, 0xffbfffff,
            0x00800000, 0x80800000, 0x7f7fffff, 0xff7fffff];
        var bits = new uint[width * height];
        for (int i = 0; i < bits.Length; i++)
        {
            uint hash = unchecked((uint)i * 0x9e3779b9u);
            hash ^= hash >> 16;
            bits[i] = i % 17 < special.Length ? special[i % 17]
                : (hash & 0x807fffffu) | ((1 + (hash >> 24) % 254) << 23);
        }
        return MemoryMarshal.AsBytes(bits.AsSpan()).ToArray();
    }

    static byte[][] Render(IGpuDevice device, IGpuTexture source, string fragment)
    {
        var owned = new List<IDisposable>();
        T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }
        try
        {
            IGpuResourceFactory factory = device.Factory;
            IGpuTexture[] targets = Enumerable.Range(0, 4).Select(_ => Own(factory.CreateTexture(
                GpuTextureDescription.Texture2D(source.Width - 1, source.Height - 1, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)))).ToArray();
            IGpuFramebuffer framebuffer = Own(factory.CreateFramebuffer(null, targets));
            IGpuResourceLayout layout = Own(factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Source", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment))));
            IGpuShaderSet shaders = Own(factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, fragment));
            IGpuPipeline pipeline = Own(factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendAttachments = Enumerable.Repeat(GpuBlendAttachment.OverrideBlend, 4).ToArray(),
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [layout],
                ShaderSet = shaders,
                VertexLayouts = [],
                Outputs = framebuffer.Outputs,
            }));
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, source,
                device.LinearSampler));
            using IGpuCommandList commands = factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, nameof(TemporalVisibilityGatherGpuTests)))
            {
                commands.SetFramebuffer(framebuffer);
                for (uint t = 0; t < 4; t++) commands.ClearColorTarget(t, Color.White);
                commands.SetPipeline(pipeline);
                commands.SetGraphicsResourceSet(0, set);
                commands.Draw(3);
            }
            device.Submit(commands);
            device.WaitForIdle();
            return targets.Select(t => GpuReadback.ToRgba(device, t, (int)t.Width, (int)t.Height)).ToArray();
        }
        finally
        {
            device.WaitForIdle();
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }
}
