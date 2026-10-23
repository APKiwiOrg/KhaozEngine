using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqCoverageHistoryInputsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_rejection_inputs_survive_resolution_and_independent_cache_changes(bool split)
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        var history = new TemporalHistory();
        history.EnsureTargets(device, 32, 24, 32, 24);
        using var retired = GpuRetireQueue.Create(device);
        using var renderer = new TemporalResolveRenderer(device);
        renderer.SelectEntry(split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused, retired);
        using var colour = Input(GpuPixelFormat.R16G16B16A16Float);
        using var depth = Input(GpuPixelFormat.R32Float);
        using var motion = Input(GpuPixelFormat.R16G16Float);
        using var ordinary = Input(GpuPixelFormat.R8UNorm);
        using var coverageA = Input(GpuPixelFormat.R8UNorm);
        using var coverageB = Input(GpuPixelFormat.R8UNorm);
        var inputs = new TemporalResolveInputs(colour, colour, depth, motion, ordinary);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        FakeResourceSet? drawn = null;
        commands.BeforeNonIndexedDraw = (bindings, _, _, _, _) =>
        {
            var set = Assert.IsType<FakeResourceSet>(bindings.Sets[0].Set);
            if (set.Resources.Length == (split ? 14 : 11)) drawn = set;
        };
        try
        {
            FakeResourceSet first = Bind(coverageA);
            FakeResourceSet second = Bind(coverageB);
            Assert.NotSame(first, second);
            int sets = factory.ResourceSets.Count, textures = factory.Textures.Count;
            Assert.Same(first, Bind(coverageA));
            Assert.Same(second, Bind(coverageB));
            Assert.Equal(sets, factory.ResourceSets.Count);
            Assert.Equal(textures, factory.Textures.Count);
        }
        finally { renderer.ReleaseSets(retired); history.ReleaseTargets(retired); }

        IGpuTexture Input(GpuPixelFormat format) => factory.CreateTexture(
            GpuTextureDescription.Texture2D(32, 24, format, GpuTextureUsage.Sampled));
        FakeResourceSet Bind(IGpuTexture coverage)
        {
            drawn = null;
            renderer.BindInputs(inputs with { CoverageRejection = coverage }, history, retired);
            renderer.Run(commands, history);
            Assert.NotNull(drawn);
            Assert.Same(coverage, drawn.Resources[^2]);
            Assert.Same(ordinary, drawn.Resources[^1]);
            if (split) Assert.Same(drawn, renderer.SplitResolveForTests!.CoverageAccumulateSetForTests(history.ReadIndex));
            return drawn;
        }
    }
}
