using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class D3D11ShaderCompileDiagnosticsTests
{
    const string Hlsl = "abc";
    const string HlslHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disabled_DoesNotRecordOrAllocate(bool previouslyEnabled)
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        if (previouslyEnabled) { using (diagnostics.Use()) { } }
        using (D3D11ShaderCompileDiagnostics.MeasureCompile("source", "program", D3D11ShaderStage.Fragment, Hlsl)) { }
        D3D11ShaderCompileDiagnostics.RecordCacheHit("source", "program", D3D11ShaderStage.Fragment);

        long allocated;
        using (AllocationRegion.Start("disabled D3D11 compile diagnostic"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using (D3D11ShaderCompileDiagnostics.MeasureCompile("source", "program", D3D11ShaderStage.Fragment, Hlsl)) { }
            D3D11ShaderCompileDiagnostics.RecordCacheHit("source", "program", D3D11ShaderStage.Fragment);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Empty(diagnostics.GetRecords());
    }

    [Theory]
    [InlineData(nameof(D3D11ShaderStage.Vertex), GpuShaderStages.Vertex)]
    [InlineData(nameof(D3D11ShaderStage.Fragment), GpuShaderStages.Fragment)]
    [InlineData(nameof(D3D11ShaderStage.Compute), GpuShaderStages.Compute)]
    public void Enabled_RecordsEachCompileAndCacheHitOnce(string stage, GpuShaderStages expectedStage)
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        var nativeStage = Enum.Parse<D3D11ShaderStage>(stage);
        using (diagnostics.Use())
        {
            using (D3D11ShaderCompileDiagnostics.MeasureCompile("source", "program", nativeStage, Hlsl)) { }
            D3D11ShaderCompileDiagnostics.RecordCacheHit("cached-source", "cached-program", nativeStage);
        }

        Assert.Collection(diagnostics.GetRecords(), compiled =>
        {
            Assert.Equal("source", compiled.SourceKey);
            Assert.Equal("program", compiled.Program);
            Assert.Equal(expectedStage, compiled.Stage);
            Assert.Equal(3, compiled.HlslByteLength);
            Assert.Equal(HlslHash, compiled.HlslSha256);
            Assert.False(compiled.CacheHit);
            Assert.True(compiled.Milliseconds >= 0);
        }, cached =>
        {
            Assert.Equal("cached-source", cached.SourceKey);
            Assert.Equal("cached-program", cached.Program);
            Assert.Equal(expectedStage, cached.Stage);
            Assert.Null(cached.HlslByteLength);
            Assert.Null(cached.HlslSha256);
            Assert.True(cached.CacheHit);
            Assert.Equal(0, cached.Milliseconds);
        });
    }

    [Fact]
    public void HlslLength_CountsUtf8Bytes()
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        using (diagnostics.Use())
        using (D3D11ShaderCompileDiagnostics.MeasureCompile("source", "program", D3D11ShaderStage.Fragment, "//\u03bb")) { }
        Assert.Equal(4, Assert.Single(diagnostics.GetRecords()).HlslByteLength);
    }

    [Fact]
    public void NestedScopes_RestoreTheirParentAndStopRecordingAfterDisposal()
    {
        var outer = new D3D11ShaderCompileDiagnostics();
        var inner = new D3D11ShaderCompileDiagnostics();
        using (outer.Use())
        {
            using (inner.Use()) D3D11ShaderCompileDiagnostics.RecordCacheHit("inner", "program", D3D11ShaderStage.Vertex);
            D3D11ShaderCompileDiagnostics.RecordCacheHit("outer", "program", D3D11ShaderStage.Fragment);
        }
        D3D11ShaderCompileDiagnostics.RecordCacheHit("outside", "program", D3D11ShaderStage.Compute);
        Assert.Equal("inner", Assert.Single(inner.GetRecords()).SourceKey);
        Assert.Equal("outer", Assert.Single(outer.GetRecords()).SourceKey);
    }

    [Fact]
    public async Task EnabledScope_FlowsToACompileWorker()
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        using (diagnostics.Use())
            await Task.Run(() => D3D11ShaderCompileDiagnostics.RecordCacheHit("worker", "program", D3D11ShaderStage.Compute));
        Assert.Equal("worker", Assert.Single(diagnostics.GetRecords()).SourceKey);
    }

    [Fact]
    public void FailedCompile_RecordsOnceAndPreservesTheException()
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        var failure = new InvalidOperationException("compile failed");
        using (diagnostics.Use())
        {
            var actual = Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var measurement = D3D11ShaderCompileDiagnostics.MeasureCompile("source", "program", D3D11ShaderStage.Fragment, Hlsl);
                throw failure;
            }));
            Assert.Same(failure, actual);
        }
        Assert.False(Assert.Single(diagnostics.GetRecords()).CacheHit);
    }

    [Fact]
    public void WindowsCompilePath_ReportsColdAndWarmStagesWithoutChangingDxbc()
    {
        if (!KhaozEngineD3D11.IsPlatformSupported) return;
        const string vertex = "#version 450\nlayout(location=0) in vec3 P; void main(){gl_Position=vec4(P,1);}";
        const string fragment = "#version 450\nlayout(location=0) out vec4 C; void main(){C=vec4(1);}";
        const string compute = "#version 450\nlayout(local_size_x=1,local_size_y=1,local_size_z=1) in; void main(){}";
        const uint flags = D3D11ShaderDebug.Optimized;
        string directory = Path.Combine(Path.GetTempPath(), "ke-d3d11-compile-diagnostics-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        try
        {
            var baselinePair = D3D11ShaderBuild.Pair(vertex, fragment, flags, null, "program");
            var baselineCompute = D3D11ShaderBuild.Compute(compute, flags, null, "compute");
            var cache = new D3D11DxbcCache(directory);
            using (diagnostics.Use())
            {
                var coldPair = D3D11ShaderBuild.Pair(vertex, fragment, flags, cache, "program");
                var coldCompute = D3D11ShaderBuild.Compute(compute, flags, cache, "compute");
                var warmPair = D3D11ShaderBuild.Pair(vertex, fragment, flags, cache, "program");
                var warmCompute = D3D11ShaderBuild.Compute(compute, flags, cache, "compute");
                Assert.Equal(baselinePair.VertexDxbc, coldPair.VertexDxbc);
                Assert.Equal(baselinePair.FragmentDxbc, coldPair.FragmentDxbc);
                Assert.Equal(baselineCompute.Dxbc, coldCompute.Dxbc);
                Assert.Equal(coldPair.VertexDxbc, warmPair.VertexDxbc);
                Assert.Equal(coldPair.FragmentDxbc, warmPair.FragmentDxbc);
                Assert.Equal(coldCompute.Dxbc, warmCompute.Dxbc);
            }
            var records = diagnostics.GetRecords();
            Assert.Equal(6, records.Length);
            Assert.All(records.Take(3), record => Assert.False(record.CacheHit));
            Assert.All(records.Skip(3), record => Assert.True(record.CacheHit));
            Assert.Equal(new[] { GpuShaderStages.Vertex, GpuShaderStages.Fragment, GpuShaderStages.Compute }, records.Take(3).Select(record => record.Stage));
            Assert.Equal(records.Take(3).Select(record => record.SourceKey), records.Skip(3).Select(record => record.SourceKey));
            Assert.All(records.Take(3), record => Assert.True(record.HlslByteLength > 0));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
