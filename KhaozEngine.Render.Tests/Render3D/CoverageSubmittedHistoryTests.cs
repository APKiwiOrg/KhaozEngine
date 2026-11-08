using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageSubmittedHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Abandoned_selection_never_replaces_submitted_history(bool failSubmission)
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        var a = f.History;
        Assert.NotNull(a.SurfaceToken);
        f.Begin(); f.Queue(20); f.Record();
        var b = Assert.Single(f.Evidence.Select(e => e.Surface).Distinct());
        if (failSubmission)
        {
            f.Device.FailSubmission = true;
            Assert.Throws<InvalidOperationException>(f.Submit);
            f.Device.FailSubmission = false;
        }
        else f.Submission.Abandon();
        Assert.Equal(a, f.History);
        f.Begin(); f.Queue(20); f.Record();
        Assert.NotEmpty(f.Evidence);
        Assert.All(f.Evidence, e => { Assert.Same(a.SurfaceToken, e.Previous); Assert.NotSame(b, e.Previous); });
        if (failSubmission) Assert.DoesNotContain(f.Evidence, e => ReferenceEquals(b, e.Surface));
        f.Submit(); Assert.Equal(1, f.History.Entry);
    }

    [Fact]
    public void Entry_to_hole_and_hole_to_entry_read_the_submitted_final_surface()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        var covered = f.History;
        f.Begin(); f.Queue(30); f.Record();
        Assert.NotEmpty(f.Evidence);
        Assert.All(f.Evidence, e => Assert.Same(covered.SurfaceToken, e.Previous));
        f.Submit(); var raw = f.History;
        Assert.Equal(-1, raw.Entry); Assert.NotNull(raw.SurfaceToken);
        Assert.NotSame(covered.SurfaceToken, raw.SurfaceToken);
        f.Begin(); f.Queue(20); f.Record();
        Assert.NotEmpty(f.Evidence);
        Assert.All(f.Evidence, e => Assert.Same(raw.SurfaceToken, e.Previous));
        f.Submit(); Assert.Equal(1, f.History.Entry);
    }

    [Fact]
    public void Default_eight_participants_commit_thirty_two_actor_histories_atomically()
    {
        using var f = new CoveragePublicationFixture();
        var actors = new List<(HostCoverageBinding Binding, MotionKey Key)> { (f.Binding, f.Host.Actor) };
        try
        {
            for (uint i = 1; i < 32; i++)
            {
                var key = MotionKey.From(901 + i);
                var binding = new HostCoverageBinding(f.Host.Adapter, f.Host.Skeleton, f.Host.Hosts, key, f.Host.Table);
                Assert.True(binding.TrySetEquipment([f.Host.Equipment(i + 100)], out _));
                actors.Add((binding, key));
            }
            f.Begin();
            foreach (var actor in actors) f.Queue(binding: actor.Binding, actor: actor.Key);
            f.Record();
            Assert.All(actors, a => Assert.Null(f.Scene.CoverageHistoryForTests(a.Key).SurfaceToken));
            f.Submit();
            Assert.Equal(32, actors.Count);
            Assert.All(actors, a => Assert.NotNull(f.Scene.CoverageHistoryForTests(a.Key).SurfaceToken));
        }
        finally { foreach (var actor in actors.Skip(1)) actor.Binding.Dispose(); }
    }

    [Fact]
    public void Full_participant_storage_commits_no_history()
    {
        using var f = new CoveragePublicationFixture(participants: 1);
        f.Begin(); Assert.True(f.Submission.TryRegister(new OccupiedParticipant()));
        f.Queue(); f.Record(); f.Submit();
        Assert.Empty(f.Evidence);
        Assert.Null(f.History.SurfaceToken);
        Assert.Equal(CoverageFailureCode.CapacityExceeded, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exhaustion_records_discontinuity_only_after_successful_ordinary_submission(bool abandon)
    {
        using var f = new CoveragePublicationFixture();
        f.Device.AutoComplete = false;
        // D is 3 on this backend, so K is 5. Each later submission still reads its predecessor.
        for (int i = 0; i < 5; i++) { f.Begin(); f.Queue(); f.Record(); f.Submit(); }
        var previous = f.History;
        Assert.NotNull(previous.SurfaceToken);
        f.Begin(); f.Queue(); f.Record();
        Assert.Equal(CoverageFailureCode.CapacityExceeded, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
        Assert.Empty(f.Evidence); Assert.Equal(previous, f.History);
        if (abandon) { f.Submission.Abandon(); Assert.Equal(previous, f.History); }
        else { f.Submit(); Assert.True(f.History.Discontinuity); }
        f.Device.WaitForIdle();
        f.Begin(); f.Queue(); f.Record();
        Assert.NotEmpty(f.Evidence);
        Assert.All(f.Evidence, e =>
        {
            if (abandon) Assert.Same(previous.SurfaceToken, e.Previous);
            else Assert.Same(e.Surface, e.Previous);
        });
        f.Submit();
    }

    [Fact]
    public void Refused_preparation_without_a_matching_draw_records_nothing()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit(); var before = f.History;
        f.Begin(); Assert.False(f.Host.Prepare(f.Binding, out _, out _, 30));
        f.Record(); f.Submit(); Assert.Equal(before, f.History);
        f.Begin(); f.Queue(); f.Record();
        Assert.NotEmpty(f.Evidence);
        Assert.All(f.Evidence, e => Assert.Same(e.Surface, e.Previous));
        f.Submit();
    }

    [Fact]
    public void A_holes_actual_submitted_palette_is_preflighted_before_raw_staging()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        f.Begin(); Assert.False(f.Host.Prepare(f.Binding, out _, out _, 30));
        var huge = f.Host.Palette.Select(_ => Matrix4x4.CreateScale(1 << 21)).ToArray();
        foreach (var mesh in f.Host.Hosts)
            f.Scene.DrawSkinned(new(mesh, Matrix4x4.Identity) { Motion = f.Host.Actor }, huge);
        f.Record();
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
        Assert.Empty(f.Evidence); Assert.False(f.History.Discontinuity);
        f.Submit(); Assert.True(f.History.Discontinuity);
    }

    [Fact]
    public void Origin_rebase_keeps_absolute_models_and_submitted_surface_correspondence()
    {
        using var f = new CoveragePublicationFixture();
        var model = Matrix4x4.CreateTranslation(100_000, 0, -100_000);
        f.Begin(); f.Queue(model: model); f.Record(); f.Submit(); var prior = f.History;
        f.Scene.RenderOrigin = new(100_000, 0, -100_000);
        f.Begin(); f.Queue(model: model); f.Record();
        Assert.NotEmpty(f.Evidence); Assert.All(f.Evidence, e => Assert.Same(prior.SurfaceToken, e.Previous));
        f.Submit(); Assert.Equal(model, f.History.Model);
    }

    [Theory]
    [InlineData("view")]
    [InlineData("incarnation")]
    public void A_replaced_view_or_actor_incarnation_seeds_previous_from_current(string replacement)
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        MotionKey actor = f.Host.Actor;
        HostCoverageBinding? binding = null;
        try
        {
            if (replacement == "view") f.Scene.CameraOverride = new IsoCamera3D();
            else
            {
                f.Binding.Dispose();
                actor = MotionKey.From(999);
                var storage = f.Device.Resources.Buffers.Where(b => b.Usage == GpuBufferUsage.StructuredBufferReadWrite).ToArray();
                binding = new(f.Host.Adapter, f.Host.Skeleton, f.Host.Hosts, actor, f.Host.Table);
                Assert.NotEmpty(storage);
                Assert.Equal(storage, f.Device.Resources.Buffers.Where(b => b.Usage == GpuBufferUsage.StructuredBufferReadWrite));
                Assert.True(binding.TrySetEquipment([f.Host.Equipment(98)], out _));
            }
            f.Begin(); f.Queue(binding: binding, actor: actor); f.Record();
            Assert.NotEmpty(f.Evidence); Assert.All(f.Evidence, e => Assert.Same(e.Surface, e.Previous));
            f.Submit();
        }
        finally { binding?.Dispose(); }
    }

    [Fact]
    public void Binding_refusal_before_host_preparation_only_discontinues_an_actually_submitted_host()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        f.Begin();
        Assert.False(f.Binding.TryPrepare(f.Host.Skeleton, f.Host.Palette, Matrix4x4.CreateScale(0), new(10),
            CoverageBindingFixture.Template, out _, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        foreach (var mesh in f.Host.Hosts)
            f.Scene.DrawSkinned(new(mesh, Matrix4x4.Identity) { Motion = f.Host.Actor }, f.Host.Palette);
        f.Record(); Assert.False(f.History.Discontinuity);
        f.Submit(); Assert.True(f.History.Discontinuity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposed_actors_keep_encoded_resources_valid_until_the_submission_boundary(bool submitted)
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record();
        if (submitted) f.Submit();
        f.Binding.Dispose();
        if (!submitted) f.Submission.Abandon();
        f.Begin(); f.Record(); f.Submit();
        Assert.Empty(f.Evidence);
        f.Scene.Dispose();
        var storage = f.Device.Resources.Buffers.Where(b => b.StructureByteStride == 4).ToArray();
        Assert.NotEmpty(storage); Assert.All(storage, b => Assert.True(b.Disposed));
    }

    [Fact]
    public void Token_free_draws_never_reuse_an_accepted_preparations_composed_palette()
    {
        using var f = new CoveragePublicationFixture();
        f.Begin(); f.Queue(); f.Record(); f.Submit();
        f.Begin(); Assert.True(f.Host.Prepare(f.Binding, out _, out _));
        var palette = f.Host.Palette.Select(p => p * Matrix4x4.CreateTranslation(3, 0, 0)).ToArray();
        foreach (var mesh in f.Host.Hosts)
            f.Scene.DrawSkinned(new(mesh, Matrix4x4.Identity) { Motion = f.Host.Actor }, palette);
        f.Record(); Assert.Empty(f.Evidence);
        f.Submit(); Assert.True(f.History.Discontinuity);
    }

    sealed class OccupiedParticipant : IGpuFrameSubmissionParticipant
    { public void Submitted() { } public void Abandoned() { } }
}
