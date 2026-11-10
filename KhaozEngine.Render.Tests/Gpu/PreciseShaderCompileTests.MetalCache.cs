using System;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class PreciseShaderCompileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metal_precise_rewrite_partitions_old_disk_entries_without_changing_default_keys(bool compute)
    {
        using var directory = new CacheDirectory();
        var cache = new MetalMslCache(directory.Path);
        var memo = new SpirvCompileCache(enabled: true, capacity: 16);
        string[] sources = compute ? [PreciseMathShaders.Compute] : [PreciseMathShaders.Vertex, PreciseMathShaders.Fragment];
        foreach (GpuShaderCompileOptions options in new[] { default, Precise })
        {
            string oldKey = PreviousKey(options);
            string currentKey = MetalShaderKey.For(options, sources);
            _output.WriteLine($"compute={compute}, precise={options.PreciseMath}, previous={oldKey}, current={currentKey}");
            Assert.Equal(!options.PreciseMath, oldKey == currentKey);
        }
        var built = Build(null);
        var oldStages = built.Stages.Select(stage => new MetalMslStage(stage.Stage, stage.EntryPointName,
            stage.Msl.Replace("[[clang::always_inline]]", "[[clang::optnone]]", StringComparison.Ordinal))).ToArray();
        var oldProgram = new MetalMslProgram(oldStages, built.Table);
        Assert.True(cache.TryStore(PreviousKey(Precise), new(oldProgram, compute ? 1u : 0u, compute ? 1u : 0u, compute ? 1u : 0u)));
        Assert.Equal(oldProgram.Stages[0].Msl, cache.TryLoad(PreviousKey(Precise), "previous")!.Program.Stages[0].Msl);
        var fresh = Build(cache);
        AssertInlinableHelpers(fresh.Stages[0].Msl);
        Assert.Equal(built.Stages[0].Msl, fresh.Stages[0].Msl);
        Assert.Equal(fresh.Stages[0].Msl, Build(cache).Stages[0].Msl);

        MetalMslProgram Build(MetalMslCache? emissionCache) => compute
            ? MetalShaderBuild.Compute(sources[0], emissionCache, label: "same-label", memo: memo, options: Precise).Program
            : MetalShaderBuild.Pair(sources[0], sources[1], emissionCache, label: "same-label", memo: memo, options: Precise);

        // The pre-change encoding, with fixed inputs except the loaded producer identities.
        string PreviousKey(GpuShaderCompileOptions options)
        {
            string text = string.Join("\n", MetalShaderKey.Schema, MetalShaderKey.EngineVersion,
                MetalShaderKey.MetalModuleId.ToString("N"), MetalShaderKey.GpuModuleId.ToString("N"), SpirvToolchainVersion.Identity,
                SpirvFrontEndPin.IdentityFor(options), MslCrossCompilePin.Identity, MslCompilePin.Identity,
                sources.Length.ToString(CultureInfo.InvariantCulture)) + "\n";
            if (options.PreciseMath) text += "precise=1\n";
            return MetalShaderKey.HashUtf8(text + string.Concat(sources.Select(source => source.Length.ToString(CultureInfo.InvariantCulture) + "\n" + source)));
        }
    }
}
