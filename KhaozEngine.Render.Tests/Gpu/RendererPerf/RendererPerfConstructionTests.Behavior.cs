using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    const string ComputeSource = "#version 450\nlayout(local_size_x = 8, local_size_y = 2, local_size_z = 1) in; void main() {}";
    static RendererPerfManifest OneOperation() => Manifest() with { Blocks = [], Controls = [] };
    static RendererPerfRecorder Recorder(int frames = 1) => new(frames, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
    static IGpuComputePipeline Compute(IGpuDevice device, string source = ComputeSource)
    {
        var shader = device.Factory.CreateComputeShaderFromSpirv(source);
        return device.Factory.CreateComputePipeline(new(shader));
    }

    [Fact]
    public void Lazy_temporal_and_specialized_ocean_are_actual_factory_inputs()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        var trace = new List<string>(); native.Trace = trace.Add;
        ActualOwners(device, native);
        using var control = new RendererPerfConstructionFixture();
        var expectedTrace = new List<string>(); control.Trace = expectedTrace.Add;
        ActualOwners(control, control);
        Assert.Equal(expectedTrace, trace);
        Assert.Equal(control.Submissions, native.Submissions);
        Assert.Equal(control.FactoryAttempts, native.FactoryAttempts);
        capture.EndOperation(); var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Equal(3, inventory.Occurrences.Count(o => o.Kind == RendererPerfPipelineKind.Graphics));
        Assert.Equal(4, inventory.Occurrences.Count(o => o.Kind == RendererPerfPipelineKind.Compute));
        var graphics = inventory.Recipes.Where(r => r.Kind == RendererPerfPipelineKind.Graphics).ToArray();
        Assert.Equal(new[] { 12, 9, 4 }, graphics.Select(r => inventory.Layouts[r.LayoutIds[0]].Elements.Length));
        var compute = inventory.Recipes.Where(r => r.Kind == RendererPerfPipelineKind.Compute).ToArray();
        Assert.Equal(new uint[] { 16, 16, 32, 32 }, compute.Select(r => inventory.Shaders[r.ShaderId].Stages[0].LocalX));
        Assert.Equal(new[] { "Params", "H0Buf", "WorkBuf" }, inventory.Layouts[compute[0].LayoutIds[0]].Elements.Select(e => e.Name));
        Assert.Equal(new[] { "Params", "WorkBuf", "FoamBuf", "OceanMap" }, inventory.Layouts[compute[1].LayoutIds[0]].Elements.Select(e => e.Name));
        Assert.All(compute, r => Assert.DoesNotContain("@HALF@", inventory.Shaders[r.ShaderId].Stages[0].Source, StringComparison.Ordinal));
        Assert.NotEqual(inventory.Shaders[compute[0].ShaderId].Stages[0].Source, inventory.Shaders[compute[2].ShaderId].Stages[0].Source);
        Assert.Equal(new long[] { 0, 0, 0, 1, 0, 1, 0 }, inventory.Occurrences.Select(o => o.BindCount));
        Assert.Equal(native.FactoryAttempts, inventory.Counts.FactoryAttempts);
        Assert.Equal(0, native.CompilerReads); Assert.Equal(0, control.CompilerReads);
        _output.WriteLine("actual temporal=3 ocean=4 factory={0} compilerReads=0 traceParity=true begins={1} dispatches={2} submissions={3}",
            native.FactoryAttempts, trace.Count(e => e == "begin"), trace.Count(e => e == "dispatch"), native.Submissions);
    }

    static void ActualOwners(IGpuDevice device, RendererPerfConstructionFixture native)
    {
        using (var resolve = new TemporalResolveRenderer(device))
        {
            Assert.Equal(0, native.GraphicsAttempts);
            var history = new TemporalHistory();
            try
            {
                history.EnsureTargets(device, 8, 8, 8, 8);
                using var colour = device.Factory.CreateTexture(new(8, 8, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled));
                using var depth = device.Factory.CreateTexture(new(8, 8, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
                using var visibility = device.Factory.CreateTexture(new(8, 8, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
                resolve.BindInputs(new(colour, colour, depth, colour, visibility), history);
                Assert.Equal(3, native.GraphicsAttempts);
                // Full, compact and store were all built by the actual lazy owner. They need not all bind.
            }
            finally { history.ReleaseTargets(); }
        }
        using (var ocean = new OceanFftProducer(device))
        {
            foreach (int size in new[] { 32, 64 })
            {
                ocean.Prepare(new WaterSettings
                {
                    WaveSource = WaterWaveSource.FftOcean,
                    SeaState = new WaterSeaState { CascadeResolution = size, CascadeCount = 1 }
                }, 0, true);
                Assert.Equal(size, ocean.Resolution);
            }
        }
    }

    [Fact]
    public void Digest_collision_and_repeated_occurrences_keep_logical_roles()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var a = Compute(device); using var b = Compute(device);
        using var c = Compute(device, ComputeSource + "\n// different final source");
        using var commands = device.Factory.CreateCommandList(); commands.SetComputePipeline(b);
        capture.EndOperation(); var inventory = capture.Freeze((_, _, _) => new string('a', 64));
        Assert.False(inventory.Complete); Assert.Contains("collision", inventory.Reason!, StringComparison.Ordinal);
        Assert.Equal(2, inventory.Recipes.Length);
        Assert.Equal(new[] { 0, 0, 1 }, inventory.Occurrences.Select(o => o.RecipeId));
        Assert.Equal(new[] { 0, 1, 2 }, inventory.Occurrences.Select(o => o.CreationOrdinal));
        Assert.Equal(new long[] { 0, 1, 0 }, inventory.Occurrences.Select(o => o.BindCount));
        Assert.Same(b, native.LastComputeBind);
    }

    [Fact]
    public void Unknown_bind_failed_creation_and_foreign_input_preserve_original_forwarding()
    {
        using var native = new RendererPerfConstructionFixture();
        using var other = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var foreign = Compute(other);
        using var commands = device.Factory.CreateCommandList(); commands.SetComputePipeline(foreign);
        Assert.Same(foreign, native.LastComputeBind);
        var first = capture.Failure;
        var original = new InvalidOperationException("actual native failure"); native.FactoryFailure = original;
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => Compute(device)));
        native.FactoryFailure = null;
        using var layout = device.Factory.CreateResourceLayout(new([]));
        Assert.Equal(first, capture.Failure);
        capture.EndOperation(); var inventory = capture.Freeze();
        Assert.False(inventory.Complete); Assert.Equal(2, native.FactoryAttempts);
        Assert.Equal(2, inventory.Counts.FactoryAttempts); Assert.Equal(2, inventory.Counts.OmittedFactoryEvents);
    }

    [Fact]
    public void Failed_pipeline_inputs_remain_detached_without_a_fabricated_witness()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var shader = device.Factory.CreateComputeShaderFromSpirv(ComputeSource);
        var failure = new InvalidOperationException("native pipeline refused"); native.FactoryFailure = failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => device.Factory.CreateComputePipeline(new(shader))));
        capture.EndOperation(); var inventory = capture.Freeze();
        var e = inventory.FactoryEvents[1]; Assert.Equal(failure.Message, e.Failure); Assert.Null(e.OutputId);
        Assert.Empty(inventory.Occurrences); Assert.Single(inventory.Recipes); Assert.Equal(2, native.FactoryAttempts);
        Assert.Equal(GpuDiagnosticStatus.Invalid, inventory.Recipes[0].CodeGenerationSha256.Status);
    }

    [Fact]
    public void Warmed_bind_observer_has_no_growth()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var pipeline = Compute(device); using var commands = device.Factory.CreateCommandList();
        commands.SetComputePipeline(pipeline);
        long bytes;
        using (AllocationRegion.Start("construction membership bind observer"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            // One bounded region. This is no timing or repeated-test/stress run.
            for (int n = 0; n < 128; n++) commands.SetComputePipeline(pipeline);
            bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Assert.Equal(0, bytes);
        capture.EndOperation(); var inventory = capture.Freeze();
        Assert.Equal(129, inventory.Occurrences[0].BindCount); Assert.Equal(0, native.CompilerReads);
        _output.WriteLine("warmed binds=128 observer currentThreadBytes={0} totalBinds=129 compilerReads=0", bytes);
    }
}
