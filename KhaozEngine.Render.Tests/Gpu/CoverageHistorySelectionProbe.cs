using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Executes the production pulling functions over the buffers and window captured from a real draw.</summary>
internal static class CoverageHistorySelectionProbe
{
    internal static uint[] Read(CoverageSceneFixture h, CoverageSceneFixture.NativeHistoryDraw draw, uint? previousBase = null)
    {
        var device = h.NativeDevice;
        using var layout = device.Factory.CreateResourceLayout(new(
            new("CoverageCurrent", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CoverageStatic", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CoverageDraw", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
            new("CoveragePrevious", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("Observation", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        string source = "#version 450\nlayout(local_size_x=64) in;\n"
            + ShaderSources.CoveragePulling.Replace("set=2", "set=0", StringComparison.Ordinal)
                .Replace("gl_VertexIndex", "int(gl_GlobalInvocationID.x)", StringComparison.Ordinal)
            + """

              layout(std430, set=0, binding=4) buffer Observation { uint observation[]; };
              void main() {
                  uint i = gl_GlobalInvocationID.x;
                  if (i >= HISTORY_CORNER_COUNT) return;
                  vec3 current = coverageVertex().position, previous = coveragePreviousPosition();
                  observation[i*6u] = floatBitsToUint(current.x);
                  observation[i*6u+1u] = floatBitsToUint(current.y);
                  observation[i*6u+2u] = floatBitsToUint(current.z);
                  observation[i*6u+3u] = floatBitsToUint(previous.x);
                  observation[i*6u+4u] = floatBitsToUint(previous.y);
                  observation[i*6u+5u] = floatBitsToUint(previous.z);
              }
              """;
        source = source.Replace("HISTORY_CORNER_COUNT", draw.CornerCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u", StringComparison.Ordinal);
        using var shader = device.Factory.CreateComputeShaderFromSpirv(source, new(PreciseMath: true));
        using var pipeline = device.Factory.CreateComputePipeline(new(shader, layout));
        using var uniform = device.Factory.CreateBuffer(new(80, GpuBufferUsage.UniformBuffer));
        var words = new uint[20]; draw.DrawWords.CopyTo(words, 0);
        if (previousBase is { } wrong) words[3] = wrong;
        device.UpdateBuffer(uniform, 0, words);
        using var output = device.Factory.CreateBuffer(new((uint)draw.CornerCount * 24, GpuBufferUsage.StructuredBufferReadWrite, 4));
        using var staging = device.Factory.CreateBuffer(new(output.SizeInBytes, GpuBufferUsage.Staging));
        using var set = device.Factory.CreateResourceSet(new(layout, draw.Surface.Current, draw.Surface.StaticData,
            uniform, draw.Surface.Previous, output));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "Coverage.HistorySelection"))
        {
            commands.SetComputePipeline(pipeline); commands.SetComputeResourceSet(0, set);
            commands.Dispatch(((uint)draw.CornerCount + 63) / 64, 1, 1);
            commands.CopyBuffer(output, 0, staging, 0, output.SizeInBytes);
        }
        device.Submit(commands); device.WaitForIdle();
        return Map(device, staging);
    }

    static unsafe uint[] Map(IGpuDevice device, IGpuBuffer staging)
    {
        var map = device.Map(staging, GpuMapMode.Read);
        try { return new ReadOnlySpan<uint>((void*)map.Data, (int)staging.SizeInBytes / 4).ToArray(); }
        finally { device.Unmap(staging); }
    }
}
