using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class WaterOpticsTriangleGpuTests(ITestOutputHelper output)
{
    const string Diagnostic = @"#version 450
#extension GL_EXT_control_flow_attributes : require
layout(local_size_x=1) in;
layout(std430,set=0,binding=0) buffer Results { uvec4 Values[]; };
layout(std140,set=0,binding=1) uniform Inputs {
    vec4 OpticsRect;
    vec4 OpticsParams;
    vec4 Queries[128];
};
layout(set=0,binding=10) uniform texture2D OpticsTex;
layout(set=0,binding=11) uniform sampler OpticsSamp;
layout(std140,set=0,binding=2) uniform Unused2 { vec4 Keep2; };
layout(std140,set=0,binding=3) uniform Unused3 { vec4 Keep3; };
layout(std140,set=0,binding=4) uniform Unused4 { vec4 Keep4; };
layout(std140,set=0,binding=5) uniform Unused5 { vec4 Keep5; };
layout(std140,set=0,binding=6) uniform Unused6 { vec4 Keep6; };
layout(std140,set=0,binding=7) uniform Unused7 { vec4 Keep7; };
layout(std140,set=0,binding=8) uniform Unused8 { vec4 Keep8; };
layout(std140,set=0,binding=9) uniform Unused9 { vec4 Keep9; };
";

    static string Source => Diagnostic + ShaderSources.WaterOpticsTriangleGlsl + @"
void main() {
    uint i = gl_GlobalInvocationID.x;
    uint guard = floatBitsToUint(Keep2.x) | floatBitsToUint(Keep3.x) | floatBitsToUint(Keep4.x)
        | floatBitsToUint(Keep5.x) | floatBitsToUint(Keep6.x) | floatBitsToUint(Keep7.x)
        | floatBitsToUint(Keep8.x) | floatBitsToUint(Keep9.x);
    if (guard != 0u) { Values[2u*i] = uvec4(guard); return; }
    vec4 q = Queries[i];
    float depth;
    bool hit = tryOpticsTriangle(q.xy, depth);
    uint a = floatBitsToUint(q.z), b = floatBitsToUint(q.w);
    Values[2u*i] = uvec4(hit ? 1u : 0u, floatBitsToUint(depth), opticsAdd(a,b), opticsAdd(a,b^0x80000000u));
    Values[2u*i+1u] = uvec4(opticsMul(a,b), opticsDiv(a,b), 0u, 0u);
}";

    [GpuFact]
    public void BothDiagonalsSignedDepthAndFinalBinsMatchOriginalGeometry()
    {
        foreach (float size in new[] { 2f, 30f, 100f })
            foreach (bool high in new[] { false, true })
                foreach (bool reverse in new[] { false, true })
                {
                    var a = new Vector3(0, -.02f, 0);
                    var b = new Vector3(size, .98f, 0);
                    var c = new Vector3(size, -.02f, size);
                    var d = new Vector3(0, .98f, size);
                    WaterOpticsTriangle[] triangles = high ? [new(a, b, c), new(a, c, d)] : [new(a, b, d), new(b, c, d)];
                    if (reverse) Array.Reverse(triangles);
                    Vector2[] points = [new(0,0), new(size,0), new(size,size), new(0,size), new(size/2),
                new(size*.75f,size*.25f), new(size*.25f,size*.75f),
                new(float.BitDecrement(size/2),size/2), new(float.BitIncrement(size/2),size/2),
                new(float.BitDecrement(size),size), new(size,float.BitDecrement(size))];
                    VerifyGeometry($"diagonal-{size}-{high}-{reverse}", size, 1024, 1, triangles, points);
                }
    }

    [GpuFact]
    public void ArbitraryVerticesThreeWayAndTJunctionHaveExactClosedContainment()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(2, 1, 0);
        var c = new Vector3(2, 2, 2);
        var d = new Vector3(0, 1, 2);
        var p = new Vector3(.7f, .3f, 1.1f);
        WaterOpticsTriangle[] fan = [new(a, b, p), new(b, c, p), new(c, d, p), new(d, a, p)];
        Vector2[] probes = [new(p.X,p.Z), new(float.BitDecrement(p.X),p.Z),
            new(float.BitIncrement(p.X),p.Z), new(p.X,float.BitDecrement(p.Z)),
            new(p.X,float.BitIncrement(p.Z)), new(.2f,.8f), new(1.7f,1.4f), new(0,2), new(2,2)];
        VerifyGeometry("interior-fan", 2, 3, 5, fan, probes);
        Array.Reverse(fan);
        VerifyGeometry("interior-fan-reversed", 2, 3, 5, fan, probes);
        var mid = new Vector3(1, 1, 1);
        WaterOpticsTriangle[] junction = [new(a, b, c), new(a, mid, d), new(mid, c, d)];
        VerifyGeometry("t-junction", 2, 3, 5, junction,
            [new(1, 1), new(float.BitDecrement(1), 1), new(float.BitIncrement(1), 1), new(.5f, .5f), new(1.5f, 1.5f)]);
        Array.Reverse(junction);
        VerifyGeometry("t-junction-reversed", 2, 3, 5, junction,
            [new(1, 1), new(float.BitDecrement(1), 1), new(float.BitIncrement(1), 1)]);
        VerifyGeometry("bin-boundaries", 2, 3, 5, fan,
            [new(2f/3,.4f),new(float.BitDecrement(2f/3),.4f),new(float.BitIncrement(2f/3),.4f),
             new(2f/3,float.BitDecrement(.4f)),new(2f/3,float.BitIncrement(.4f))]);
        var three = new Vector3(1.1f, .3f, .7f);
        WaterOpticsTriangle[] threeWay = [new(a, b, three), new(b, c, three), new(c, a, three)];
        VerifyGeometry("three-way", 2, 3, 5, threeWay,
            [new(three.X, three.Z), new(float.BitDecrement(three.X), three.Z),
             new(float.BitIncrement(three.X), three.Z), new(three.X, float.BitDecrement(three.Z)),
             new(three.X, float.BitIncrement(three.Z))], new(new(1, .5f), new(1.25f, .75f)));
    }

    [GpuFact]
    public void ThirtyTwoCandidatesReachTheLastOrdinalInEitherSourceOrder()
    {
        var triangles = new List<WaterOpticsTriangle>();
        for (int i = 0; i < 16; i++)
        {
            float lo = i / 16f, hi = (i + 1) / 16f;
            float near = 30 + (i & 1), far = 30 + ((i + 1) & 1);
            triangles.Add(new(new(lo, near, 0), new(hi, far, 0), new(hi, far, 1)));
            triangles.Add(new(new(lo, near, 0), new(hi, far, 1), new(lo, near, 1)));
        }
        Vector2[] points = [new(31f / 32, .99f), new(.01f, .01f), new(.5f), new(0, 0), new(1, 1)];
        VerifyGeometry("maximum-occupancy", 1, 1, 1, triangles.ToArray(), points);
        triangles.Reverse();
        VerifyGeometry("maximum-occupancy-reversed", 1, 1, 1, triangles.ToArray(), points);
    }

    [GpuFact]
    public void FinalAtlasTexelAndClosedBinEdgeDoNotTurnOutsidePointsIntoHits()
    {
        WaterOpticsTriangle[] triangles = [new(new(0, 2, 0), new(1, 2, 0), new(1, 2, 1)),
            new(new(0, 2, 0), new(1, 2, 1), new(0, 2, 1))];
        var field = new WaterOpticsTriangleField(Vector2.One, 497, 1, triangles, [new(Vector2.Zero, Vector2.One)]);
        Assert.Equal(1024, field.TextureWidth);
        Assert.Equal(2, field.TextureHeight);
        Assert.Equal(2048, (int)field.AtlasScalars[8]);
        uint[] values = Capture("final-atlas-texel", field,
            [new(1, 1, 1, 3), new(float.BitIncrement(1), .5f, 1, 3), new(1, float.BitIncrement(1), 1, 3)]);
        Assert.Equal(1u, values[0]);
        Assert.Equal(2f, BitConverter.UInt32BitsToSingle(values[1]));
        Assert.Equal(0u, values[8]);
        Assert.Equal(0u, values[16]);
    }

    [GpuFact]
    public void ExactPredicatesHandleProductsOutsideNativeFloatRange()
    {
        foreach (float size in new[] { 1E-30f, 1E30f })
        {
            WaterOpticsTriangle[] triangles = [new(new(0,30,0),new(size,30,0),new(size,30,size)),
                new(new(0,30,0),new(size,30,size),new(0,30,size))];
            VerifyGeometry($"exponent-range-{size}", size, 3, 5, triangles,
                [new(0,0),new(size/2),new(float.BitDecrement(size/2),size/2),
                 new(float.BitIncrement(size/2),size/2),new(size,size)]);
            var field = new WaterOpticsTriangleField(new(size), 3, 5, triangles, [new(Vector2.Zero, new(size))]);
            uint[] outside = Capture($"exponent-misses-{size}", field,
                [new(float.BitIncrement(size), size / 2, 1, 3), new(size / 2, float.BitIncrement(size), 1, 3)]);
            Assert.Equal(0u, outside[0]);
            Assert.Equal(0u, outside[8]);
        }
    }

    [GpuFact]
    public void RepresentativeDepthsMatchDoubleRayAndMissStatusIsIndependentOfSignedDepth()
    {
        foreach (float depth in new[] { -1f, 0f, 2f, 30f, 100f })
        {
            WaterOpticsTriangle[] triangles =
                [new(new(0,depth,0),new(2,depth+.0078125f,0),new(2,depth+.0234375f,2)),
                 new(new(0,depth,0),new(2,depth+.0234375f,2),new(0,depth+.015625f,2))];
            VerifyGeometry($"signed-depth-{depth}", 2, 2, 3, triangles,
                [new(0, 0), new(.5f, 1.5f), new(2, 2), new(2, 0)]);
        }
        WaterOpticsTriangle[] island = [new(new(.25f, -1, .25f), new(.75f, -1, .25f), new(.25f, -1, .75f))];
        var field = new WaterOpticsTriangleField(Vector2.One, 1, 1, island, [new(new(.25f), new(.375f))]);
        uint[] values = Capture("explicit-miss", field, [new(.3f, .3f, 1, 1), new(.9f, .9f, 1, 1)]);
        Assert.Equal(1u, values[0]);
        Assert.Equal(-1f, BitConverter.UInt32BitsToSingle(values[1]));
        Assert.Equal(0u, values[8]);
    }

    [GpuFact]
    public void IntegerBinary32OperationsPreserveNearestEvenIncludingDivisionAndSubnormals()
    {
        float tiny = BitConverter.UInt32BitsToSingle(1);
        Vector2[] operands = [new(1,3),new(1,10),new(7,11),new(100,30),new(-1,3),
            new(1,float.BitIncrement(1)),new(float.BitDecrement(1),float.BitIncrement(1)),
            new(1,-float.BitDecrement(1)),new(1,-float.BitIncrement(1)),
            new(tiny,1),new(tiny,2),new(tiny*3,2),new(1.17549435E-38f,2),
            new(1.17549435E-38f,-float.BitDecrement(1.17549435E-38f)),
            new(float.MaxValue,2),new(float.MaxValue,-float.MaxValue),new(-0f,1),new(0,-1)];
        WaterOpticsTriangle[] triangles = [new(new(0,0,0),new(2,0,0),new(2,0,2)),
            new(new(0,0,0),new(2,0,2),new(0,0,2))];
        var field = new WaterOpticsTriangleField(new(2), 1, 1, triangles, [new(Vector2.Zero, new(2))]);
        uint[] values = Capture("binary32", field, operands.Select(p => new Vector4(1, 1, p.X, p.Y)).ToArray());
        for (int i = 0; i < operands.Length; i++)
        {
            float a = operands[i].X, b = operands[i].Y;
            AssertBits(a + b, values[8 * i + 2], i, "add");
            AssertBits(a - b, values[8 * i + 3], i, "subtract");
            AssertBits(a * b, values[8 * i + 4], i, "multiply");
            if (b != 0) AssertBits(a / b, values[8 * i + 5], i, "divide");
        }
    }

    [GpuFact]
    public void WaterFragmentHelperClampsSignedDepthOnlyAfterDisplacementAndReferenceHeight()
    {
        foreach (float origin in new[] { 0f, 16384f })
        {
            float[] depths = CapturePhysical(origin);
            Assert.Equal(new float[] { 0, 1, 0, 2 }, depths);
        }
    }

    float[] CapturePhysical(float origin, WaterOpticsTriangleField? field = null, Vector4[]? queries = null,
        Vector4? rectangle = null, string? evidenceName = null)
    {
        field ??= KhaozEngine.Tests.Render3D.WaterOpticsTriangleResourceTests.Field();
        queries ??= [new(origin + .25f, -origin + .25f, 3, 999),
            new(origin + .5f, -origin + .5f, 5, 999),
            new(origin + 1.5f, -origin + 1.5f, 2, 999), new(origin + 2, -origin + 2, 6, 999)];
        Assert.InRange(queries.Length, 1, 128);
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        IGpuResourceFactory f = device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle handle = resources.LoadTriangles(field);
        var uniforms = new Vector4[130];
        uniforms[0] = rectangle ?? new(origin, -origin, .5f, .5f);
        uniforms[1] = new(3, 2, field.TextureWidth, field.TextureHeight);
        queries.CopyTo(uniforms, 2);
        using IGpuBuffer input = f.CreateBuffer(new GpuBufferDescription(130 * 16, GpuBufferUsage.UniformBuffer));
        using IGpuBuffer zero = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
        device.UpdateBuffer(input, 0, uniforms);
        device.UpdateBuffer(zero, 0, new Vector4[1]);
        var elements = new GpuResourceLayoutElement[12];
        var bindings = new IGpuBindableResource[12];
        string source = @"#version 450
#extension GL_EXT_control_flow_attributes : require
layout(location=0) out vec4 Output;
layout(std140,set=0,binding=0) uniform Inputs { vec4 OpticsRect; vec4 OpticsParams; vec4 Queries[128]; };
";
        string guard = "0u";
        for (int i = 0; i < 10; i++)
        {
            elements[i] = new(i == 0 ? "Inputs" : "Unused" + i, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);
            bindings[i] = i == 0 ? input : zero;
            if (i == 0) continue;
            source += $"layout(std140,set=0,binding={i}) uniform Unused{i} {{ vec4 Keep{i}; }};\n";
            guard += $" | floatBitsToUint(Keep{i}.x)";
        }
        source += ShaderSources.WaterTriangleOpticsGlsl + @"
void main() {
    vec4 q = Queries[int(gl_FragCoord.x)];
    float depth = opticalBedDepth(vec3(q.x,q.z,q.y),q.w);
    uint bits = floatBitsToUint(depth) ^ (" + guard + @");
    Output = vec4(bits&255u,(bits>>8u)&255u,(bits>>16u)&255u,bits>>24u)/255.0;
}";
        elements[10] = new("OpticsTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        elements[11] = new("OpticsSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        bindings[10] = resources.Resolve(handle).Texture;
        bindings[11] = device.PointSampler;
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(elements));
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, bindings));
        using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D((uint)queries.Length, 1,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer framebuffer = f.CreateFramebuffer(null, target);
        using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, source);
        using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(),
            Outputs = framebuffer.Outputs
        });
        using (IGpuCommandList commands = f.CreateCommandList())
        {
            commands.Begin();
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, Color.Black);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
        }
        byte[] rgba = GpuReadback.ToRgba(device, target, queries.Length, 1);
        var depths = new float[queries.Length];
        for (int i = 0; i < depths.Length; i++) depths[i] = BitConverter.ToSingle(rgba, i * 4);
        string directory = "/tmp/khaozengine-water-triangles/task-2/" + device.Backend;
        Directory.CreateDirectory(directory);
        string name = evidenceName ?? $"physical-{origin}";
        File.WriteAllBytes(Path.Combine(directory, name + ".rgba"), rgba);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(depths));
        return depths;
    }

    void AssertBits(float expected, uint actual, int probe, string operation) => Assert.True(
        BitConverter.SingleToUInt32Bits(expected) == actual,
        $"{operation} probe {probe}: expected 0x{BitConverter.SingleToUInt32Bits(expected):X8}, got 0x{actual:X8}");

    void VerifyGeometry(string name, float size, int columns, int rows, WaterOpticsTriangle[] triangles, Vector2[] points,
        WaterOpticsRectangle? certificate = null)
    {
        var field = new WaterOpticsTriangleField(new(size), columns, rows, triangles,
            [certificate ?? new(Vector2.Zero, new(size))]);
        uint[] actual = Capture(name, field, points.Select(p => new Vector4(p, 1, 3)).ToArray());
        for (int i = 0; i < points.Length; i++)
        {
            Assert.True(actual[8 * i] == 1, $"{name} missed original-geometry point {points[i]}");
            double expected = RayDepth(triangles, points[i]);
            float depth = BitConverter.UInt32BitsToSingle(actual[8 * i + 1]);
            Assert.True(Math.Abs(depth - expected) <= .0001,
                $"{name} at {points[i]}: depth {depth:R}, original triangle ray {expected:R}");
        }
    }

    // Original binary32 vertices lifted to binary64. Never reads packed data or the CPU packed sampler.
    static double RayDepth(WaterOpticsTriangle[] triangles, Vector2 p)
    {
        foreach (WaterOpticsTriangle t in triangles)
        {
            double dx = (double)t.B.X - t.A.X, dz = (double)t.B.Z - t.A.Z;
            double ex = (double)t.C.X - t.A.X, ez = (double)t.C.Z - t.A.Z;
            double px = (double)p.X - t.A.X, pz = (double)p.Y - t.A.Z;
            double det = dx * ez - dz * ex;
            double b = (px * ez - pz * ex) / det, c = (dx * pz - dz * px) / det;
            if (b < 0 || c < 0 || b + c > 1) continue;
            return t.A.Y + b * ((double)t.B.Y - t.A.Y) + c * ((double)t.C.Y - t.A.Y);
        }
        throw new InvalidOperationException("Original-geometry ray missed a test point.");
    }

    uint[] Capture(string name, WaterOpticsTriangleField field, Vector4[] queries, string? diagnosticSource = null)
    {
        Assert.InRange(queries.Length, 1, 128);
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        IGpuResourceFactory f = device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle handle = resources.LoadTriangles(field);
        var uniforms = new Vector4[130];
        uniforms[0] = new(0, 0, 1 / field.Size.X, 1 / field.Size.Y);
        uniforms[1] = new(0, 2, field.TextureWidth, field.TextureHeight);
        queries.CopyTo(uniforms, 2);
        using IGpuBuffer input = f.CreateBuffer(new GpuBufferDescription(130 * 16, GpuBufferUsage.UniformBuffer));
        device.UpdateBuffer(input, 0, uniforms);
        using IGpuBuffer result = f.CreateBuffer(new GpuBufferDescription((uint)queries.Length * 32,
            GpuBufferUsage.StructuredBufferReadWrite, 16));
        var elements = new GpuResourceLayoutElement[12];
        var bindings = new IGpuBindableResource[12];
        elements[0] = new("Results", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute);
        bindings[0] = result;
        for (int i = 1; i < 10; i++)
        {
            elements[i] = new(i == 1 ? "Inputs" : "Unused" + i, GpuResourceKind.UniformBuffer, GpuShaderStages.Compute);
            bindings[i] = input;
        }
        elements[10] = new("OpticsTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Compute);
        elements[11] = new("OpticsSamp", GpuResourceKind.Sampler, GpuShaderStages.Compute);
        bindings[10] = resources.Resolve(handle).Texture;
        bindings[11] = device.PointSampler;
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(elements));
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, bindings));
        using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(diagnosticSource ?? Source);
        using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
        var elapsed = Stopwatch.StartNew();
        using (IGpuCommandList commands = f.CreateCommandList())
        {
            commands.Begin();
            commands.SetComputePipeline(pipeline);
            commands.SetComputeResourceSet(0, set);
            commands.Dispatch((uint)queries.Length, 1, 1);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
        }
        uint[] values = GpuReadback.ReadBuffer<uint>(device, result, queries.Length * 8);
        elapsed.Stop();
        string directory = "/tmp/khaozengine-water-triangles/task-2/" + device.Backend;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".bin"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            device.Backend,
            device.Capabilities.DeviceName,
            field.MaximumBinOccupancy,
            field.TextureBytes,
            Queries = queries.Select(q => new[] { q.X, q.Y }).ToArray(),
            Values = values,
            DispatchAndReadbackMilliseconds = elapsed.Elapsed.TotalMilliseconds
        }));
        output.WriteLine($"{name}: {device.Backend}, K={field.MaximumBinOccupancy}, {queries.Length} queries, {elapsed.Elapsed.TotalMilliseconds:F3} ms dispatch and readback");
        if (!File.Exists(Path.Combine(directory, "water-fragment.spv"))
            || !File.Exists(Path.Combine(directory, "diagnostic.glsl"))
            || File.ReadAllText(Path.Combine(directory, "diagnostic.glsl")) != Source)
        {
            File.WriteAllText(Path.Combine(directory, "diagnostic.glsl"), Source);
            File.WriteAllBytes(Path.Combine(directory, "diagnostic.spv"), SpirvFrontEnd.ToSpirv(Source, GpuShaderStages.Compute));
            File.WriteAllText(Path.Combine(directory, "diagnostic.hlsl"), SpirvCrossCompile.GlslComputeToHlsl(Source).ComputeSource);
            File.WriteAllText(Path.Combine(directory, "diagnostic.msl"), SpirvCrossCompile.ComputeToMsl(
                SpirvFrontEnd.ToSpirv(Source, GpuShaderStages.Compute)).ComputeSource);
            byte[] vertex = SpirvFrontEnd.ToSpirv(ShaderSources.WaterVert, GpuShaderStages.Vertex);
            byte[] fragment = SpirvFrontEnd.ToSpirv(ShaderSources.WaterTriangleFrag, GpuShaderStages.Fragment);
            File.WriteAllBytes(Path.Combine(directory, "water-fragment.spv"), fragment);
            File.WriteAllText(Path.Combine(directory, "water-fragment.hlsl"),
                SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment).FragmentSource);
            File.WriteAllText(Path.Combine(directory, "water-fragment.msl"),
                SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment).FragmentSource);
        }
        return values;
    }
}
