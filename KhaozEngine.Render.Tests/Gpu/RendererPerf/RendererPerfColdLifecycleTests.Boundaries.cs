using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Theory]
    [InlineData("create.production", false)]
    [InlineData("identify", false)]
    [InlineData("frame", false)]
    [InlineData("production.context.dispose", false)]
    [InlineData("prepare", false)]
    [InlineData("create.diagnostic", false)]
    [InlineData("diagnostic.shader", false)]
    [InlineData("diagnostic.layout", false)]
    [InlineData("diagnostic.pipeline", false)]
    [InlineData("diagnostic.read", false)]
    [InlineData("reserve", false)]
    [InlineData("commit", false)]
    [InlineData("diagnostic.context.dispose", false)]
    [InlineData("diagnostic.dispose.pipeline", false)]
    [InlineData("publish", false)]
    [InlineData("diagnostic.shader", true)]
    [InlineData("diagnostic.read", true)]
    public void Every_boundary_checks_one_deadline_and_never_starts_next_item(string boundary, bool cancel)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        rig.Boundary = stage =>
        {
            if (stage != boundary) return;
            if (cancel) rig.Cancellation.Cancel(); else rig.Now = 600100;
        };
        var outcome = rig.Run();
        Assert.Contains(outcome.Failures, f => cancel ? f.Error is OperationCanceledException : f.Error is TimeoutException);
        Assert.True(rig.Frames <= 1260);
        Assert.All(rig.Trace.Select((stage, index) => (stage, index)).Where(p => p.stage.EndsWith("context.dispose", StringComparison.Ordinal)),
            p => Assert.True(p.index < rig.Trace.IndexOf(outcome.Report is null ? "early" : "publish")));
        if (boundary.StartsWith("diagnostic.", StringComparison.Ordinal) && boundary != "diagnostic.context.dispose")
        {
            Assert.Equal(1, rig.Diagnostic.ShaderAttempts);
            Assert.True(rig.Diagnostic.ComputeAttempts <= 1);
            Assert.True(rig.Diagnostic.CompilerReads <= 1);
            Assert.Equal(1, rig.Publication.Reserved);
        }
        if (outcome.Report is { } report)
        {
            Assert.False(report.Publication.FinalUnitPublished);
            Assert.Equal(cancel ? 0 : 600000, report.Publication.ElapsedMilliseconds.Value);
            Assert.Equal(0, report.Publication.EstimatedCostUsd.Value - (cancel ? 0 : 10) * (double)report.Manifest.Budget.RatePerMinute);
        }
    }

    [Fact]
    public void Refused_preparation_or_read_reservation_creates_no_graph()
    {
        using (var rig = new RendererPerfColdLifecycleFixture())
        {
            rig.Publication.Ready = false;
            var outcome = rig.Run();
            Assert.Single(rig.Attempts); Assert.Equal(0, rig.Diagnostic.FactoryAttempts);
            Assert.Contains(outcome.Failures, f => f.Error.Message.Contains("preparation", StringComparison.Ordinal));
        }
        using (var rig = new RendererPerfColdLifecycleFixture())
        {
            rig.Publication.RefuseReservation = true;
            var outcome = rig.Run();
            Assert.Equal(0, rig.Diagnostic.FactoryAttempts); Assert.Equal(0, rig.Diagnostic.CompilerReads);
            Assert.Equal(2, outcome.Report!.Compiler.Results.Length);
            Assert.All(outcome.Report.Compiler.Results, r => Assert.Null(r.Read));
        }
    }

    [Fact]
    public void Production_final_observation_precedes_disposal_and_excludes_diagnostics()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        bool transient = false;
        rig.Boundary = stage =>
        {
            if (stage != "frame" || transient) return;
            transient = true;
            using var allocation = rig.Production.Memory.TrackBuffer(new(4096, GpuBufferUsage.VertexBuffer), 1);
        };
        using var huge = rig.Diagnostic.Memory.TrackBuffer(new(900000, GpuBufferUsage.VertexBuffer), 1);
        var outcome = rig.Run();
        Assert.Empty(outcome.Failures);
        var memory = Assert.Single(outcome.Report!.Memory.Observations, o => o.Stage == "unit-final");
        Assert.Equal("unit", memory.Operation); Assert.Null(memory.FirstFrame);
        var native = Assert.Single(memory.Native);
        Assert.True(transient);
        Assert.Equal(4096UL, native.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.DoesNotContain("diagnostic.memory", rig.Trace);
        Assert.Equal(0UL, native.LiveResourceCount.Value!.UInt64);
        Assert.Equal(outcome.Report.Memory.Observations[^1], memory);
        Assert.True(rig.Trace.LastIndexOf("production.memory") < rig.Trace.IndexOf("production.context.dispose"));
        RendererPerfMemoryEvidence.Validate(outcome.Report);
        Assert.Null(typeof(GpuResourceMemorySnapshot).GetProperty("PeakLiveResourceCount"));
    }

    [Fact]
    public void Scene_constructor_retains_original_and_attachment_cleanup_failure()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var construct = new InvalidOperationException("target creation"); var cleanup = new InvalidOperationException("texture cleanup");
        rig.Boundary = stage =>
        {
            if (stage == "production.framebuffer") throw construct;
            if (stage == "production.dispose.texture") throw cleanup;
        };
        using var recipe = RendererPerfRecipe.Create("empty");
        var f = recipe.Facts;
        var identity = new RendererPerfRecipeIdentity("S0", f.Id + "/" + f.Version, f.Profile, f.RecipeSha256,
            f.SettingsSha256, new string('b', 64), f.GeometrySha256, f.AssetSha256);
        rig.WorkFactory = (device, _, observer) => new RendererPerfSceneWorkload(device, observer, identity);
        var outcome = rig.Run();
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(construct, outcome.Failures[0].Error); Assert.Same(cleanup, outcome.Failures[1].Error);
        Assert.Equal("renderer.scene.construct", outcome.Failures[0].Stage);
        Assert.Equal(0, rig.Diagnostic.CompilerReads);
    }

    [Fact]
    public void Scene_disposal_drain_failure_never_destroys_children()
    {
        using var device = new FakeGpuDevice();
        var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var textures = ((FakeGpuResourceFactory)device.Factory).Textures.ToArray();
        var failure = new InvalidOperationException("drain");
        device.OnWaitForIdle = () => throw failure;
        var transport = Assert.Throws<GpuDiagnosticFailureException>(scene.Dispose);
        Assert.Same(failure, Assert.Single(transport.Failures).Error);
        Assert.False(scene.DisposalSafeForDependants);
        Assert.All(textures, t => Assert.False(t.Disposed));
        device.OnWaitForIdle = null;
        scene.Dispose();
    }
    [Fact]
    public void Captured_graphics_state_replays_through_actual_factories_and_bounded_reader()
    {
        var manifest = RendererPerfConstructionTests.Manifest() with { Blocks = [], Controls = [] };
        var capture = new RendererPerfConstructionCapture(manifest);
        using var production = new RendererPerfConstructionFixture();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
        var device = RendererPerfDevice.Wrap(production, recorder, capture);
        capture.BeginOperation(0);
        using var shaders = device.Factory.CreateShadersFromSpirv("vertex source", "fragment source");
        using var layout = device.Factory.CreateResourceLayout(new([new("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, true)]));
        using var pipeline = device.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = [layout],
            VertexLayouts = [new(12, 1, [new("Position", GpuVertexElementFormat.Float3)])],
            BlendAttachments = [GpuBlendAttachment.AlphaBlend],
            Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm),
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.CounterClockwise, true, true),
            Topology = GpuPrimitiveTopology.TriangleList
        });
        capture.EndOperation(); var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        var diagnostic = new RendererPerfConstructionFixture
        {
            Configuration = RendererPerfConstructionFixture.Diagnostic,
            CompilerApi = new RendererPerfColdLifecycleFixture.ExecutableApi
            { Stages = Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit }
        };
        using var context = new GpuDeviceContext(diagnostic, null, null,
            new(GpuBackendKind.VulkanNative, GpuBackendSource.UserPreference, null), true);
        int commits = 0;
        var cold = RendererPerfColdCollector.Collect(inventory, RendererPerfSchedules.For("pilot"), context, production.Configuration,
            new(new(() => 0, 1000), 0, 1000, default), backend => RendererPerfCompilerReadContract.ForAvailable(backend, 1048576),
            (reservation, bytes) => { Assert.True(bytes > 0 && bytes <= reservation.MaximumEncodedBytes); commits++; });
        Assert.Empty(cold.Failures); Assert.Equal(1, commits);
        Assert.Equal(RendererPerfCollectionState.Complete, cold.Compiler.State);
        Assert.Equal(1, diagnostic.GraphicsAttempts); Assert.Equal(0, diagnostic.ComputeAttempts);
        Assert.Empty(RendererPerfCompilerCorrespondence.Validate(inventory, cold.Pipelines, cold.Compiler,
            RendererPerfSchedules.For("pilot").Operations, RendererPerfSchedules.For("pilot").ConditioningOperation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_or_expiry_before_creation_opens_no_device(bool cancel)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        if (cancel) rig.Cancellation.Cancel(); else rig.Now = 600100;
        var outcome = rig.Run();
        Assert.Null(outcome.Report); Assert.NotNull(outcome.EarlyFailure);
        Assert.Empty(rig.Attempts); Assert.Empty(rig.BackendProvider.Settings);
        Assert.Equal(0, rig.Frames); Assert.Equal(1, rig.Publication.EarlyWrites);
    }

    [Fact]
    public void Scene_disposal_attempts_every_remaining_safe_owned_texture_in_order()
    {
        using var device = new FakeGpuDevice();
        var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.LoadTexture(new byte[4], 1, 1); scene.LoadTexture(new byte[4], 1, 1);
        var textures = ((FakeGpuResourceFactory)device.Factory).Textures.TakeLast(2).ToArray();
        var first = new InvalidOperationException("first texture"); var second = new InvalidOperationException("second texture");
        textures[0].OnDispose = () => throw first; textures[1].OnDispose = () => throw second;
        var failures = Assert.Throws<GpuDiagnosticFailureException>(scene.Dispose).Failures;
        Assert.True(scene.DisposalSafeForDependants);
        Assert.Equal(2, failures.Length); Assert.Same(first, failures[0].Error); Assert.Same(second, failures[1].Error);
        textures[0].OnDispose = textures[1].OnDispose = null;
        textures[0].Dispose(); textures[1].Dispose();
    }

}
