using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfControlShaderTests
{
    [Fact]
    public void Corpus_retains_all_control_work_for_spirv_hlsl_and_msl()
    {
        var corpus = RendererPerfControlShaders.Corpus;
        Assert.Equal(6, corpus.Length);
        Assert.All(corpus, row =>
        {
            Assert.Equal(64, row.SourceSha256.Length); Assert.Equal(64, row.SpirvSha256.Length);
            Assert.Equal(64, row.HlslSha256.Length); Assert.Equal(64, row.MslSha256.Length);
            Assert.True(row.Stores > 0);
        });
        Assert.Equal(128, corpus.Single(row => row.Name == "fma.comp").FmaInstructions);
        Assert.Equal(64, corpus.Single(row => row.Name == "dependent.comp").Divides);
        Assert.Equal(64, corpus.Single(row => row.Name == "dependent.comp").Roots);
        // A removed store is refused by the retention gate rather than becoming measured throughput.
        byte[] module = KhaozEngine.Gpu.Internal.SpirvFrontEnd.ToSpirv("#version 450\nlayout(local_size_x=64) in; void main() {}",
            KhaozEngine.Gpu.GpuShaderStages.Compute, "removed control output");
        Assert.Equal(0, RendererPerfControlShaders.Count(module, 62));
        string? directory = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_TASK12_EVIDENCE");
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "control-corpus.json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(corpus));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "controls.json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, 12).Select(RendererPerfControls.Definition).ToArray()));
        }
    }
}
