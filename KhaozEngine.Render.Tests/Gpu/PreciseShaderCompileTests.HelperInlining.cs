using System;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class PreciseShaderCompileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metal_helper_variants_coexist_in_disk_and_frontend_caches(bool compute)
    {
        using var directory = new CacheDirectory();
        var cache = new MetalMslCache(directory.Path);
        var memo = new SpirvCompileCache(enabled: true, capacity: 16);
        string[] sources = compute ? [PreciseMathShaders.Compute] : [PreciseMathShaders.Vertex, PreciseMathShaders.Fragment];
        string shippedKey = MetalShaderKey.For(Precise, sources);
        string inlineKey = MetalShaderKey.ForInlineHelpers(Precise, sources);
        Assert.NotEqual(shippedKey, inlineKey);
        var shipped = Build(false);
        long shippedCompiles = memo.CompileCount;
        var inlined = Build(true);
        Assert.True(memo.CompileCount > shippedCompiles);
        Assert.Equal(shipped.Stages[0].Msl.Replace("[[clang::optnone]]", "[[clang::always_inline]]", StringComparison.Ordinal), inlined.Stages[0].Msl);
        Assert.Equal(shipped.Stages[0].Msl, Build(false).Stages[0].Msl);
        Assert.Equal(inlined.Stages[0].Msl, Build(true).Stages[0].Msl);
        Assert.Equal(shipped.Stages[0].Msl, cache.TryLoad(shippedKey, "shipped")!.Program.Stages[0].Msl);
        Assert.Equal(inlined.Stages[0].Msl, cache.TryLoad(inlineKey, "inlined")!.Program.Stages[0].Msl);

        MetalMslProgram Build(bool inline) => compute
            ? MetalShaderBuild.Compute(sources[0], cache, label: "same-label", memo: memo, options: Precise, inlineHelpers: inline).Program
            : MetalShaderBuild.Pair(sources[0], sources[1], cache, label: "same-label", memo: memo, options: Precise, inlineHelpers: inline);
    }
}
