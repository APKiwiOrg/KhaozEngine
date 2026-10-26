using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfSceneScheduleTests(ITestOutputHelper output)
{
    [Fact]
    public void Rehearsal_schedule_retains_real_scene_conditioning_and_every_protocol_boundary()
    {
        var template = RendererPerfRehearsalExecutionTests.Report();
        using var recipe = RendererPerfRecipe.Create("crowd");
        var facts = recipe.Facts;
        var identity = template.Manifest.Recipes.Single() with
        {
            Version = facts.Id + "/" + facts.Version,
            Profile = facts.Profile,
            RecipeSha256 = facts.RecipeSha256,
            SettingsSha256 = facts.SettingsSha256,
            GeometrySha256 = facts.GeometrySha256,
            AssetSha256 = facts.AssetSha256
        };
        template = template with
        {
            Manifest = template.Manifest with { Recipes = [identity] },
            Identity = template.Identity with { TimestampPeriodNs = 2.5 }
        };
        using var fixture = new RendererPerfColdLifecycleFixture(template);
        fixture.Production.Software = true;
        fixture.Production.Features = new(false, true, "llvmpipe", supportsCompute: true,
            supportsShadowMaps: true, supportsCompletionFences: true)
        { SupportsTimestamps = true, MaxColorAttachments = 8 };
        fixture.Production.Configuration = fixture.Production.Configuration with { PipelineStatisticsSupported = false };
        fixture.Diagnostic.Configuration = fixture.Production.Configuration with { Requested = new(true, false) };
        var passes = RendererPerfFrozenLayout.Create(identity);
        var frames = new List<(string Scene, RendererPerfLayout Layout, int Index)>();
        int validated = 0;
        fixture.PassDefinitions = block => block.SceneId.StartsWith("control:", StringComparison.Ordinal) ? [] : passes;
        fixture.WorkFactory = (device, block, observer) =>
        {
            // The inert device does not execute control shaders or provide their native output readback.
            // Existing fixture controls still exercise all six complete session and capture lifecycles.
            if (block.SceneId.StartsWith("control:", StringComparison.Ordinal)) return null;
            var work = new RendererPerfSceneWorkload(device, observer, identity, (_, _) => validated++);
            return new CountingScene(work, index =>
            {
                fixture.Frames++;
                frames.Add((block.SceneId, block.Layout, index));
            });
        };

        var stopwatch = Stopwatch.StartNew();
        var outcome = fixture.Run();
        output.WriteLine("Whole schedule elapsed {0:F3} seconds, retained scene frames {1}, total frames {2}.",
            stopwatch.Elapsed.TotalSeconds, frames.Count, fixture.Frames);
        Assert.True(outcome.Failures.IsEmpty, string.Join(Environment.NewLine, outcome.Failures.Select(f => f.Error.Message)));
        var report = Assert.IsType<RendererPerfReport>(outcome.Report);
        Assert.Equal(1260, fixture.Frames);
        Assert.Equal(9, fixture.CreatedSessions);
        Assert.Equal(684, frames.Count);
        Assert.Equal(3, validated);
        Assert.Equal(Enumerable.Range(0, 300), frames.Take(300).Select(f => f.Index));
        Assert.Equal(Enumerable.Range(-64, 192), frames.Skip(300).Take(192).Select(f => f.Index));
        Assert.Equal(Enumerable.Range(-64, 192), frames.Skip(492).Select(f => f.Index));
        Assert.Equal(RendererPerfLayout.PerPass, frames[492].Layout);
        Assert.Equal(300, report.Conditioning.Length);
        Assert.Equal(420, report.Batches.Length);
        Assert.All(report.Batches, batch => Assert.Equal(3, batch.Count));
        Assert.Equal(new[] { 1, 4, 5, 7, 9, 11 }, report.Controls.Select(c => c.Control.Id));
        Assert.All(report.Controls, control =>
        {
            Assert.Equal(32, control.Warmup.Length);
            Assert.Equal(64, control.Measured.Length);
        });
        var samples = report.Conditioning.Concat(report.Blocks.SelectMany(b => b.Warmup.Concat(b.Measured)))
            .Concat(report.Controls.SelectMany(c => c.Warmup.Concat(c.Measured))).ToArray();
        Assert.Equal(1260, samples.Length);
        Assert.All(samples, sample =>
        {
            Assert.Empty(sample.Errors);
            Assert.Equal(1, sample.Metrics["logical.total.Submissions"].Value);
            Assert.Equal(0, sample.Metrics["logical.total.WaitForIdleCalls"].Value);
            Assert.Equal(RendererPerfMetricStatus.Available, sample.GpuFrame.Status);
        });
        Assert.All(report.Blocks.SelectMany(b => b.Measured).Concat(report.Controls.SelectMany(c => c.Measured)),
            sample => Assert.Equal(0, sample.Metrics["logical.total.ResourceCreations"].Value));
        Assert.True(report.Conditioning[0].Metrics["logical.total.ResourceCreations"].Value > 0);
        var inventory = Assert.IsType<RendererPerfCompilerInventory>(outcome.Inventory);
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Contains(inventory.Occurrences, o => o.OperationOrdinal == 0 && o.FrameIndex == 0
            && o.Stage == RendererPerfConstructionStage.Cold);
        Assert.Contains(inventory.Occurrences, o => o.OperationOrdinal == 2 && o.FrameIndex == -64
            && o.Stage == RendererPerfConstructionStage.Warmup);
        Assert.DoesNotContain(inventory.Occurrences, o => o.Stage == RendererPerfConstructionStage.Measured);
        Assert.Equal(RendererPerfCollectionState.Complete, report.Compiler.State);
        Assert.Empty(RendererPerfCompilerCorrespondence.Validate(inventory, report.Pipelines, report.Compiler));
    }

    sealed class CountingScene(RendererPerfSceneWorkload work, Action<int> count) : IRendererPerfNativeWork
    {
        public IGpuFramebuffer Target => work.Target;
        public Color ClearColor => work.ClearColor;
        public RendererPerfRecipeReport Recipe => work.Recipe;
        public void PrepareCold(int index) => work.PrepareCold(index);
        public void Prepare(int index) { count(index); work.Prepare(index); }
        public void Prepare(int index, GpuFrameSubmission submission) { count(index); work.Prepare(index, submission); }
        public void Record(IGpuCommandList commands, int index) => work.Record(commands, index);
        public void ValidateOutput() => work.ValidateOutput();
        public void Dispose() => work.Dispose();
    }
}
