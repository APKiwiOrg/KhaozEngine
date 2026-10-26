using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfSceneWaitTests
{
    [Fact]
    public void Conditioning_retires_cold_replacements_without_waits_or_extra_submissions()
    {
        using var device = CreateDevice();
        using var session = CreateSession(device, "conditioning:S3", 0, 3);
        var samples = new RendererPerfFrameSample[3];
        session.Frame(0, ref samples[0]);
        session.Frame(1, ref samples[1]);
        session.Frame(2, ref samples[2]);
        session.CompleteBatch(samples);

        Assert.All(samples, sample => Assert.Equal(0, sample.Metrics["logical.total.WaitForIdleCalls"].Value));
        Assert.True(samples[0].Metrics["logical.total.ResourceCreations"].Value > 0);
        Assert.All(samples, sample => Assert.Equal(1, sample.Metrics["logical.total.Submissions"].Value));
        Assert.All(samples, sample => Assert.Empty(sample.Errors));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(192)]
    public void Fresh_scene_replacements_never_drain_warmup_or_measured_frames(int warmup)
    {
        using var device = CreateDevice();
        using var session = CreateSession(device, "S3", warmup, 1);
        var samples = new RendererPerfFrameSample[1];
        session.Frame(-warmup, ref samples[0]);
        session.CompleteBatch(samples);

        Assert.Equal(0, samples[0].Metrics["logical.total.WaitForIdleCalls"].Value);
        Assert.Equal(1, samples[0].Metrics["logical.total.Submissions"].Value);
        if (warmup == 0) Assert.Contains("Hot resource growth occurred in a steady frame.", samples[0].Errors);
        else Assert.Empty(samples[0].Errors);
    }

    [Fact]
    public void Counted_warmup_leaves_measured_frames_without_waits_growth_or_extra_submissions()
    {
        using var device = CreateDevice();
        using var session = CreateSession(device, "S3", 6, 3);
        for (int first = -6; first < 3; first += 3)
        {
            var samples = new RendererPerfFrameSample[3];
            for (int i = 0; i < 3; i++) session.Frame(first + i, ref samples[i]);
            session.CompleteBatch(samples);
            Assert.All(samples, sample => Assert.Equal(0, sample.Metrics["logical.total.WaitForIdleCalls"].Value));
            Assert.All(samples, sample => Assert.Equal(1, sample.Metrics["logical.total.Submissions"].Value));
            Assert.All(samples, sample => Assert.Empty(sample.Errors));
            if (first >= 0)
                Assert.All(samples, sample => Assert.Equal(0, sample.Metrics["logical.total.ResourceCreations"].Value));
        }
    }

    [Fact]
    public void Conditioning_refuses_an_injected_production_wait()
    {
        using var device = CreateDevice();
        using var session = CreateSession(device, "conditioning:S3", 0, 1, injectWait: true);
        var samples = new RendererPerfFrameSample[1];
        session.Frame(0, ref samples[0]); session.CompleteBatch(samples);
        Assert.Equal(1, samples[0].Metrics["logical.total.WaitForIdleCalls"].Value);
        Assert.Equal(1, samples[0].Metrics["logical.total.Submissions"].Value);
        Assert.Contains("An unexpected production wait occurred.", samples[0].Errors);
    }

    static GpuPerfTimestampTestDevice CreateDevice() => new()
    {
        Features = new(false, true, "NVIDIA T4", supportsCompletionFences: true,
            supportsCompute: true, supportsShadowMaps: true)
        { SupportsTimestamps = true, MaxColorAttachments = 8 }
    };

    static RendererPerfNativeSession CreateSession(GpuPerfTimestampTestDevice device, string sceneId,
        int warmup, int measured, bool injectWait = false)
    {
        using var recipe = RendererPerfRecipe.Create("crowd");
        var facts = recipe.Facts;
        var recipeIdentity = new RendererPerfRecipeIdentity("S3", "crowd/1", facts.Profile, facts.RecipeSha256,
            facts.SettingsSha256, new string('b', 64), facts.GeometrySha256, facts.AssetSha256);
        var identity = RendererPerfProtocolTests.CompleteReport().Identity with { TimestampPeriodNs = 2.5 };
        long tick = 0;
        return new RendererPerfNativeSession(device,
            new(identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty),
            new(sceneId, RendererPerfLayout.TotalOnly, 0, warmup, measured), [], new(() => tick++, 1000),
            (wrapped, observer) =>
            {
                var work = new RendererPerfSceneWorkload(wrapped, observer, recipeIdentity, (_, _) => { });
                return injectWait ? new WaitingWork(wrapped, work) : work;
            });
    }
    sealed class WaitingWork(IGpuDevice device, RendererPerfSceneWorkload work) : IRendererPerfNativeWork
    {
        public IGpuFramebuffer Target => work.Target;
        public KhaozEngine.Primitives.Color ClearColor => work.ClearColor;
        public RendererPerfRecipeReport? Recipe => work.Recipe;
        public void PrepareCold(int index) => work.PrepareCold(index);
        public void Prepare(int index) { device.WaitForIdle(); work.Prepare(index); }
        public void Prepare(int index, GpuFrameSubmission submission) { device.WaitForIdle(); work.Prepare(index, submission); }
        public void Record(IGpuCommandList commands, int index) => work.Record(commands, index);
        public void ValidateOutput() => work.ValidateOutput();
        public void Dispose() => work.Dispose();
    }

}
