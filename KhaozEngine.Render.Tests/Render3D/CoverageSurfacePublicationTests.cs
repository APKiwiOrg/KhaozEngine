using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageSurfacePublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Logical_surface_history_tokens_follow_recorded_actor_passes(bool gpuSkinning, bool shadowOnly)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpuSkinning;
        f.Scene.ForceTemporalForTests = true;
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        f.Begin(); f.Queue(outlines: true, shadowOnly: shadowOnly); f.Record();
        Assert.NotEmpty(f.Evidence);
        foreach (var pass in shadowOnly ? new[] { "cascade", "full", "visible" } : new[] { "motion", "cascade", "full", "visible" })
            Assert.Contains(f.Evidence, e => e.Pass == pass);
        Assert.All(f.Evidence, e =>
        {
            Assert.Equal(f.Host.Actor, e.Actor); Assert.True(e.IndexCount > 0);
            Assert.Same(f.Evidence[0].Surface, e.Surface);
            Assert.Contains(f.Host.Topology.MaterialRanges, r => r.FirstIndex == e.FirstIndex && r.IndexCount == e.IndexCount);
        });
        if (shadowOnly) Assert.DoesNotContain(f.Evidence, e => e.Pass is "main" or "motion");
        f.Submit(); Assert.Same(f.Evidence[0].Surface, f.History.SurfaceToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Point_visible_and_shadow_only_actors_publish_the_same_surface_on_both_skinning_routes(bool gpu, bool cameraCulled)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpu;
        f.Scene.FrustumCulling = cameraCulled;
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var position = cameraCulled ? f.Scene.Camera.Eye + new Vector3(100, 0, 0) : Vector3.Zero;
        f.Begin();
        f.Queue(shadowOnly: !cameraCulled, model: Matrix4x4.CreateTranslation(position));
        f.Scene.AddLight(position + Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record(); f.Submit(); // Point atlases are brought up at the next scene boundary.
        f.Begin(); f.Queue(shadowOnly: !cameraCulled, model: Matrix4x4.CreateTranslation(position));
        f.Scene.AddLight(position + Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record();
        Assert.Contains(f.Evidence, e => e.Pass == "point");
        Assert.All(f.Evidence, e => { Assert.False(e.VisibleMain); Assert.Same(f.Evidence[0].Surface, e.Surface); });
        f.Submit(); Assert.Same(f.Evidence[0].Surface, f.History.SurfaceToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reordered_logical_history_tokens_stay_with_their_actor(bool gpu)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpu;
        var actor = MotionKey.From(902);
        using var second = new HostCoverageBinding(f.Host.Adapter, f.Host.Skeleton, f.Host.Hosts, actor, f.Host.Table);
        Assert.True(second.TrySetEquipment([f.Host.Equipment(18)], out _));
        f.Begin(); f.Queue(); f.Queue(20, binding: second, actor: actor); f.Record(); f.Submit();
        var a = f.History.SurfaceToken;
        var b = f.Scene.CoverageHistoryForTests(actor).SurfaceToken;
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotSame(a, b);
        f.Begin(); f.Queue(binding: second, actor: actor); f.Queue(20); f.Record();
        Assert.Equal(2, f.Evidence.Select(e => e.Actor).Distinct().Count());
        Assert.All(f.Evidence, e =>
        {
            Assert.Same(e.Actor == actor ? b : a, e.Previous);
            Assert.Contains(f.Host.Topology.MaterialRanges, r => r.FirstIndex == e.FirstIndex && r.IndexCount == e.IndexCount);
        });
        f.Submit();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Sticky_device_refusal_preserves_ordinary_draws_and_programs(bool compute, bool completion, bool precise)
    {
        using var f = new CoveragePublicationFixture(compute: compute, completion: completion, precise: precise);
        f.Scene.ForceTemporalForTests = true;
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        Assert.Empty(f.Evidence); Assert.Null(f.History.SurfaceToken);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
        Assert.True(f.Commands.PipelineBinds.Count > 0);
        Assert.DoesNotContain(f.Device.Resources.GraphicsPipelines, p => p.VertexGlsl.Contains("CoverageCurrent", StringComparison.Ordinal));
        f.Device.RefusePrecise = false;
        f.Begin(); f.Queue(30); f.Record(); f.Submit();
        Assert.Empty(f.Evidence); Assert.Null(f.History.SurfaceToken);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
    }
    [Fact]
    public void Sticky_refusal_records_byte_identical_ordinary_uploads_to_an_unprepared_control()
    {
        using var refused = new CoveragePublicationFixture(precise: false);
        using var control = new CoveragePublicationFixture(compute: false);
        using var a = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        using var b = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        refused.Scene.ForceTemporalForTests = control.Scene.ForceTemporalForTests = true;
        refused.Scene.Post.Quality.Shadows.Mode = control.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        for (int frame = 0; frame < 2; frame++)
        {
            refused.Begin(); control.Begin(); a.Clear(); b.Clear();
            refused.Queue(30);
            foreach (var mesh in control.Host.Hosts)
                control.Scene.DrawSkinned(new(mesh, Matrix4x4.Identity) { Motion = control.Host.Actor }, control.Host.Palette);
            RecordOrdinary(refused, a); RecordOrdinary(control, b);
            Assert.NotEmpty(a.Uploads); Assert.NotEmpty(b.Uploads);
            Assert.Equal(b.Uploads.Select(u => (u.Offset, u.Bytes)), a.Uploads.Select(u => (u.Offset, u.Bytes)));
            Assert.Equal(b.Uploads.SelectMany(u => u.Data!).ToArray(), a.Uploads.SelectMany(u => u.Data!).ToArray());
        }
    }

    static void RecordOrdinary(CoveragePublicationFixture fixture, RecordingGpuCommandList commands)
    {
        fixture.Scene.PrepareFrame(128, 96);
        commands.Begin(); fixture.Scene.RenderInternal(commands, 128, 96, fixture.Target); commands.End();
        fixture.Submission.Submit(commands);
    }

    [Fact]
    public void Ordinary_skinned_headers_survive_shared_surface_publication_in_the_same_recording()
    {
        using var f = new CoveragePublicationFixture();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        { CapturePayloads = true, CaptureBindings = true, CaptureInstancedBindings = true };
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        f.Begin(); f.Queue();
        var ordinaryModel = Matrix4x4.CreateTranslation(7, 2, 0);
        f.Scene.DrawSkinned(new(f.Host.Hosts[0], ordinaryModel), f.Host.Palette);
        f.Scene.PrepareFrame(128, 96);
        commands.Begin(); f.Scene.RenderInternal(commands, 128, 96, f.Target); commands.End();
        byte[] expected = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in ordinaryModel, 1)).ToArray();
        var matching = commands.Uploads.Where(u => u.Data is { Length: >= 64 }
            && u.Data.AsSpan(0, 64).SequenceEqual(expected)).ToArray();
        Assert.NotEmpty(matching);
        var first = matching[0];
        var last = commands.Uploads.Last(u => ReferenceEquals(u.Buffer, first.Buffer));
        Assert.True(last.DrawsBefore > first.DrawsBefore);
        Assert.Equal(expected, last.Data!.AsSpan(0, 64).ToArray());
        var pulled = commands.Bindings.Where(b => b.Pipeline is FakePipeline { Request: { } r }
            && r.VertexGlsl.Contains("CoverageCurrent", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(pulled);
        Assert.All(pulled, b => Assert.NotEqual(0u, b.Sets[0].DynamicOffset));
        f.Submission.Submit(commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rebased_shadow_only_surfaces_are_classified_in_cascade_space(bool gpu)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpu;
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        var origin = new Vector3(100_000, 0, -100_000);
        f.Scene.Camera.Target = origin;
        f.Scene.RenderOrigin = origin;
        f.Begin(); f.Queue(shadowOnly: true, model: Matrix4x4.CreateTranslation(origin)); f.Record();
        Assert.Contains(f.Evidence, e => e.Pass == "cascade");
        Assert.DoesNotContain(f.Evidence, e => e.VisibleMain);
        f.Submit();
    }

    [Fact]
    public void Invalidation_expires_tokens_but_preserves_an_already_prepared_surface_snapshot()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Scene.PrepareFrame(128, 96);
        Assert.True(f.Binding.TrySetEquipment([f.Host.Equipment(99)], out _));
        f.Record(); Assert.NotEmpty(f.Evidence); f.Submit();
        f.Begin(); f.Queue(); f.Record();
        Assert.NotEmpty(f.Evidence); Assert.All(f.Evidence, e => Assert.Same(e.Surface, e.Previous));
        f.Submit();
    }

}
