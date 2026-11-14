using System;
using System.Numerics;
using System.Linq;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalDetailTransportTests
{
    [Fact]
    public void A_failed_fine_allocation_does_not_publish_a_successful_binding()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        var history = new TemporalHistory();
        try
        {
            history.EnsureTargets(device, 4, 4, 4, 4, detail: TemporalHistoryDetail.Fine);
            history.BeginResolve(0);
            using IGpuTexture motion = factory.CreateTexture(GpuTextureDescription.Texture2D(4, 4,
                GpuPixelFormat.R16G16Float, GpuTextureUsage.Sampled));
            using var renderer = new TemporalResolveRenderer(device);
            var inputs = new TemporalResolveInputs(history.Color(0), history.Color(0), history.PreviousDepth(0), motion);
            factory.ThrowOnTextureFormat = GpuPixelFormat.R32G32B32A32Float;
            Assert.Throws<InvalidOperationException>(() => renderer.BindInputs(inputs, history));
            Assert.False(renderer.HoldsSetsForTests);
            factory.ThrowOnTextureFormat = null;
            renderer.BindInputs(inputs, history);
            using IGpuCommandList commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, nameof(TemporalDetailTransportTests)))
            {
                renderer.PrepareUniforms(commands, new TemporalResolveUniforms
                {
                    Sizes = new Vector4(4, 4, 4, 4), Jitter = new Vector4(0, 0, 1, 0),
                }, new TemporalDepthStoreUniforms());
                renderer.Run(commands, history);
            }
            Assert.NotNull(renderer.CurrentSet);
            Assert.Equal(TemporalResolveEntry.Fused, renderer.LastEntry);
        }
        finally { history.ReleaseTargets(); }
    }

    [Fact]
    public void Fine_debug_views_and_counts_replay_the_fine_history_fetch()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        rig.Scene.DebugView = SceneDebugView.Shimmer;
        rig.Scene.RequestTemporalCounts();
        rig.Frame();
        string[] recorded = rig.Factory.GraphicsPipelines.Select(p => p.FragmentGlsl).ToArray();
        var precision = TemporalResolvePrecisionPolicy.For(rig.Device);
        Assert.Contains(TemporalVisibilityGatherPolicy.ForBackend(
            ShaderSources.TemporalDetailDebugFragment(precision), rig.Device.Backend), recorded);
        Assert.Contains(TemporalVisibilityGatherPolicy.ForBackend(
            ShaderSources.TemporalDetailProbeFragment(precision), rig.Device.Backend), recorded);
    }

    [Fact]
    public void Fine_uses_its_own_programs_without_allocating_unused_split_targets()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        rig.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        rig.Frame();
        var renderer = rig.Scene.TemporalResolveRendererForTests;
        Assert.NotNull(renderer);
        Assert.False(renderer.SplitTargetsAllocated);
        Assert.Equal(TemporalResolveEntry.Fused, renderer.LastEntry);
        Assert.Equal(3, renderer.LastEntryFragmentsForTests().Length);
    }

    [Fact]
    public void Adding_distrust_never_admits_a_fine_pixel()
    {
        foreach (float motion in new[] { 0f, 0.00003f, 0.0001f, 0.25f, 0.5f, 8f })
            foreach (bool locked in new[] { false, true })
                for (int before = 0; before < 32; before++)
                    for (int added = 0; added < 32; added++)
                    {
                        bool old = TemporalDetailMath.UseFine(motion, locked, before, 0);
                        bool next = TemporalDetailMath.UseFine(motion, locked, before | added, 0);
                        Assert.True(old || !next);
                        Assert.Equal(next, TemporalDetailMath.UseFine(motion, locked, before, added));
                    }
        Assert.True(TemporalDetailMath.UseFine(1f, false, 0, 0));
        Assert.False(TemporalDetailMath.UseFine(0f, false, 0, 0));
        Assert.False(TemporalDetailMath.UseFine(0.25f, true, 0, 0));
        Assert.True(TemporalDetailMath.UseFine(1f, false, 4 | 8 | 16, 4 | 8 | 16));
    }

    [Fact]
    public void Retention_is_bounded_and_rejected_history_has_no_share()
    {
        var random = new Random(713);
        for (int i = 0; i < 2048; i++)
        {
            Vector3 r = Colour(random), c = Colour(random), h = Colour(random);
            float weight = random.NextSingle();
            float k = TemporalDetailMath.Retention(r, c, h, weight, true);
            Assert.InRange(k, 0f, 1f - weight);
            Assert.Equal(0f, TemporalDetailMath.Retention(r, c, h, weight, false));
        }
        Assert.Equal(0.75f, TemporalDetailMath.Retention(Vector3.One, Vector3.One, Vector3.One, 0.25f, true));
        Assert.Equal(0.5f, TemporalDetailMath.Retention(Vector3.UnitX, Vector3.Zero, new Vector3(1f, 1f, 0f), 0.25f, true));
        Assert.Equal(0.80019534f, TemporalDetailMath.Retention(new Vector3(1f / 1024f, 2f, 0f), Vector3.Zero,
            new Vector3(1f, 2f, 0f), 0.125f, true));
        Assert.InRange(TemporalDetailMath.Retention(new Vector3(float.MaxValue), Vector3.Zero,
            new Vector3(float.MaxValue), 0.25f, true), 0f, 0.75f);
        Assert.Equal(0f, TemporalDetailMath.Retention(new Vector3(float.NaN), Vector3.Zero, Vector3.One, 0f, true));
        Assert.Equal(0f, TemporalDetailMath.Retention(Vector3.One, Vector3.Zero, new Vector3(float.PositiveInfinity), 0f, true));
    }

    [Fact]
    public void Bounded_transport_conserves_the_stored_mean_over_the_entire_half_range()
    {
        // Both accepted counterexamples. Equal values, zero, subnormal and top-of-range R are separate rows.
        Check(1f / 1024f, new Vector4(0f, 0.25f, 0.75f, 3f), 0.80019534f);
        Check(23008f, new Vector4(65504f, 0f, 0f, 0f), 0.875f);
        Assert.Equal(new Vector4(0f, 1f / 4096f, 3f / 4096f, 3f / 1024f),
            TemporalDetailMath.Transport(1f / 1024f, new Vector4(0f, 0.25f, 0.75f, 3f), 0.80019534f));
        Assert.Equal(65504f, TemporalDetailMath.Transport(23008f, new Vector4(65504f, 0f, 0f, 0f), 0.875f).X);
        // An interpolated footprint's mean can round down to its smallest fetch at float32 precision.
        Check((float)Half.Epsilon, new Vector4(1024f, float.BitIncrement(1024f),
            float.BitIncrement(1024f), float.BitIncrement(1024f)), 1f);
        foreach (float r in new[] { 0f, (float)Half.Epsilon, 1f / 16384f, 1f, 16376f, 65504f })
        {
            Check(r, new Vector4(r), 1f);
            Check(r, new Vector4(65504f, 0f, (float)Half.Epsilon, 16f), 1f);
        }
        var random = new Random(14027);
        for (int i = 0; i < 12000; i++)
            Check(Stored(random), new Vector4(Stored(random), Stored(random), Stored(random), Stored(random)),
                random.NextSingle());
    }

    [Fact]
    public void Unsafe_transport_replicates_R_without_arithmetic()
    {
        foreach (float r in new[] { -1f, float.PositiveInfinity, float.NaN })
        {
            Vector4 stored = TemporalDetailMath.Transport(r, Vector4.One, 0.5f);
            for (int i = 0; i < 4; i++) Assert.Equal(BitConverter.SingleToInt32Bits(r), BitConverter.SingleToInt32Bits(stored[i]));
        }
        Assert.Equal(new Vector4(2f), TemporalDetailMath.Transport(2f, new Vector4(float.NaN), 0.5f));
        Assert.Equal(new Vector4(2f), TemporalDetailMath.Transport(2f, new Vector4(float.PositiveInfinity), 0.5f));
    }

    static void Check(float r, Vector4 fetched, float scalar)
    {
        float retention = TemporalDetailMath.ChannelRetention(r, fetched, scalar);
        Assert.InRange(retention, 0f, scalar);
        float mean = ((fetched.X + fetched.Y) + (fetched.Z + fetched.W)) * 0.25f;
        for (int i = 0; i < 4; i++)
            Assert.InRange(r + retention * (fetched[i] - mean), 0f, 65504f);
        Vector4 stored = TemporalDetailMath.Transport(r, fetched, scalar);
        for (int i = 0; i < 4; i++) Assert.InRange(stored[i], 0f, 65504f);
        double actual = ((double)stored.X + stored.Y + stored.Z + stored.W) / 4.0;
        Half half = (Half)r;
        double up = half == Half.MaxValue ? 32.0 : (double)(float)Half.BitIncrement(half) - r;
        double step = Math.Max(up, r - (double)(float)Half.BitDecrement(half));
        Assert.True(Math.Abs(actual - r) <= step,
            $"R={r:R}, fetch={fetched}, scalar={scalar:R}, retention={retention:R}, stored={stored}, mean={actual:R}, step={step:R}");
    }

    static float Stored(Random random) => (float)BitConverter.Int16BitsToHalf((short)random.Next(0, 0x7c00));
    static Vector3 Colour(Random random) => new(Stored(random), Stored(random), Stored(random));
}
