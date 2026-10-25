using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One GPU query result: whether the shipped triangle helper found a containing triangle, and the signed
/// rest depth it interpolated. Misses report status only, never a depth.</summary>
internal readonly record struct TileWaterGpuSample(bool Hit, float Depth);

/// <summary>Runs the shipped mode-2 triangle helper on the GPU over one field, at field-local XZ points. The field is
/// the immutable instance the adapter uploaded. The helper source is the one the water fragment compiles.</summary>
internal static class TileWorldWaterTopologyGpuProbe
{
    const int Batch = 128;

    const string Header = @"#version 450
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

    // The unused blocks only hold the shared twelve-element layout in place. They alias the input block, whose
    // first scalar is the zero field-local rectangle minimum, so the guard reads zero.
    static readonly string Source = Header + ShaderSources.WaterOpticsTriangleGlsl + @"
void main() {
    uint i = gl_GlobalInvocationID.x;
    uint guard = floatBitsToUint(Keep2.x) | floatBitsToUint(Keep3.x) | floatBitsToUint(Keep4.x)
        | floatBitsToUint(Keep5.x) | floatBitsToUint(Keep6.x) | floatBitsToUint(Keep7.x)
        | floatBitsToUint(Keep8.x) | floatBitsToUint(Keep9.x);
    if (guard != 0u) { Values[i] = uvec4(2u, guard, 0u, 0u); return; }
    float depth = 0.0;
    bool hit = tryOpticsTriangle(Queries[i].xy, depth);
    Values[i] = uvec4(hit ? 1u : 0u, hit ? floatBitsToUint(depth) : 0u, 0u, 0u);
}";

    internal static TileWaterGpuSample[] Query(IGpuDevice device, WaterOpticsTriangleField field, IReadOnlyList<Vector2> local)
    {
        IGpuResourceFactory f = device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle handle = resources.LoadTriangles(field);
        using IGpuBuffer input = f.CreateBuffer(new GpuBufferDescription(130 * 16, GpuBufferUsage.UniformBuffer));
        using IGpuBuffer result = f.CreateBuffer(new GpuBufferDescription(Batch * 16,
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
        using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(Source);
        using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
        var samples = new TileWaterGpuSample[local.Count];
        var uniforms = new Vector4[130];
        uniforms[1] = new(0, 2, field.TextureWidth, field.TextureHeight);
        for (int start = 0; start < local.Count; start += Batch)
        {
            int count = Math.Min(Batch, local.Count - start);
            uniforms[0] = new(0, 0, 1 / field.Size.X, 1 / field.Size.Y);
            Array.Clear(uniforms, 2, Batch);
            for (int i = 0; i < count; i++) uniforms[2 + i] = new(local[start + i], 0, 0);
            device.UpdateBuffer(input, 0, uniforms);
            using (IGpuCommandList commands = f.CreateCommandList())
            {
                commands.Begin();
                commands.SetComputePipeline(pipeline);
                commands.SetComputeResourceSet(0, set);
                commands.Dispatch((uint)count, 1, 1);
                commands.End();
                device.Submit(commands);
                device.WaitForIdle();
            }
            uint[] values = GpuReadback.ReadBuffer<uint>(device, result, count * 4);
            for (int i = 0; i < count; i++)
            {
                if (values[4 * i] > 1) throw new InvalidOperationException("The diagnostic layout guard was not zero.");
                samples[start + i] = new(values[4 * i] == 1, BitConverter.UInt32BitsToSingle(values[4 * i + 1]));
            }
        }
        return samples;
    }
}
