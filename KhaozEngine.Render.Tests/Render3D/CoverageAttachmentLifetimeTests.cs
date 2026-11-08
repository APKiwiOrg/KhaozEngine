using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageAttachmentLifetimeTests
{
    [Fact]
    public void Warm_actor_preparation_and_holes_allocate_no_managed_memory()
    {
        using var f = new CoverageBindingFixture(mixed: true, materialSlices: true);
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out _));
        f.Scene.Begin();
        Assert.False(f.Prepare(binding, out _, out _, 99));
        f.Scene.Begin();
        f.Source.OnRead = _ => throw new InvalidOperationException("Preparation cannot read assets.");
        bool prepared, hole;
        long allocated;
        using (AllocationRegion.Start("warm actor table preparation and hole"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            prepared = f.Prepare(binding, out _, out _);
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        f.Scene.Begin();
        using (AllocationRegion.Start("warm actor table hole"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            hole = f.Prepare(binding, out _, out _, 99);
            allocated += GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Assert.True(prepared);
        Assert.False(hole);
        Assert.Equal(0, allocated);
        Assert.True(binding.PreparedEquipmentDraws.IsEmpty);
    }

    [Fact]
    public void Repeated_binding_preparation_cannot_rewrite_a_queued_snapshot()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var frame, out _));
        f.Scene.DrawSkinned(f.Host(frame), f.Palette);
        f.Palette[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        Assert.False(f.Prepare(binding, out var replacement, out var failure, 20));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.Equal(default, replacement);
        Assert.Equal(Matrix4x4.Identity, frame.Preparation!.Palette[0]);
        Assert.True(binding.PreparedEquipmentDraws.IsEmpty);
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out _, 20));
    }

    [Fact]
    public void Disposing_replaced_cache_does_not_invalidate_current_equipment()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        using var cache = new KhaozEngine.Attachments.Render3D.AttachmentSetCache(f.Adapter, f.Source);
        var loaded = cache.Get("equipment.json", f.Loaded.DefinitionSha256);
        var coverage = cache.GetCoverage(loaded, "coverage.json", f.Coverage.CompanionSha256);
        Assert.True(binding.TrySetEquipment([new(18, loaded.Bind(f.Map, f.Meshes[0]), coverage)], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var frame, out _));
        f.Cache.Dispose();
        f.Scene.DrawSkinned(f.Host(frame), f.Palette);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Repeated_low_level_preparation_cannot_rewrite_a_live_snapshot()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var frame, out _));
        var p = frame.Preparation!;
        Matrix4x4[] changed = f.Palette.AsSpan().ToArray();
        changed[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        Assert.False(p.Owner.TryPrepare(changed, Matrix4x4.Identity, new(20), p.Commands.AsSpan(0, p.CommandCount),
            f.Actor, out var second, out var failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.Equal(default, second);
        f.Scene.DrawSkinned(f.Host(frame), f.Palette);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Shared_sets_keep_actor_palettes_and_complete_material_snapshots_separate()
    {
        using var f = new CoverageBindingFixture(mixed: true, materialSlices: true);
        using var first = f.Binding();
        using var second = new KhaozEngine.Attachments.Render3D.HostCoverageBinding(f.Adapter, f.Skeleton, f.Hosts, MotionKey.From(902), f.Table);
        Assert.True(first.TrySetEquipment([f.Equipment()], out _));
        Assert.True(second.TrySetEquipment([f.Equipment(18)], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(first, out var frame, out _));
        var borrowed = first.PreparedEquipmentDraws;
        Assert.Equal(3, borrowed.Length);
        Assert.False(borrowed.Span[0].IsSkinned);
        Assert.False(borrowed.Span[1].IsSkinned);
        Assert.True(borrowed.Span[2].IsSkinned);
        f.Palette[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        Assert.True(f.Prepare(second, out _, out _));
        Assert.Equal(Matrix4x4.Identity, borrowed.Span[2].BoneMatrices.Span[0]);
        Assert.Equal(Matrix4x4.CreateTranslation(1, 0, 0), second.PreparedEquipmentDraws.Span[2].BoneMatrices.Span[0]);
        f.Scene.Draw(borrowed.Span[0].Rigid);
        f.Scene.Draw(borrowed.Span[1].Rigid);
        f.Scene.DrawSkinned(borrowed.Span[2].Skinned, borrowed.Span[2].BoneMatrices.Span);
        Assert.Equal(2, f.Scene.QueuedInstancesForTests.Count);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(0, f.Scene.CoverageEntryForTests(frame));
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("replace")]
    [InlineData("unload")]
    [InlineData("equipment")]
    [InlineData("dispose")]
    [InlineData("cache")]
    public void Tokens_expire_with_their_frame_configuration_or_resources(string change)
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var token, out _));
        if (change == "begin") f.Scene.Begin();
        if (change == "replace") Assert.True(binding.TrySetEquipment([], out _));
        if (change == "unload") f.Scene.UnloadSkinnedMesh(f.Hosts[0]);
        if (change == "equipment") f.Adapter.UnloadRigidMeshes();
        if (change == "dispose") binding.Dispose();
        if (change == "cache") f.Cache.Dispose();
        var error = Assert.Throws<CoverageFrameStateException>(() => f.Scene.DrawSkinned(f.Host(token), f.Palette));
        Assert.Equal(CoverageFailureCode.StaleFrame, error.Reason);
        Assert.Empty(f.Scene.QueuedSkinnedInstancesForTests);
    }

    [Theory]
    [InlineData("palette")]
    [InlineData("model")]
    [InlineData("actor")]
    public void Draw_cannot_change_the_frozen_pose(string change)
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var token, out _));
        var draw = f.Host(token);
        if (change == "palette") f.Palette[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        if (change == "model") draw = draw with { Model = Matrix4x4.CreateTranslation(1, 0, 0) };
        if (change == "actor") draw = draw with { Motion = MotionKey.From(903) };
        var error = Assert.Throws<CoverageFrameStateException>(() => f.Scene.DrawSkinned(draw, f.Palette));
        Assert.Equal(CoverageFailureCode.StaleFrame, error.Reason);
        Assert.Empty(f.Scene.QueuedSkinnedInstancesForTests);
        f.Palette[0] = Matrix4x4.Identity;
        f.Scene.DrawSkinned(f.Host(token), f.Palette);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
    }

    [Fact]
    public void Equipment_descriptors_are_frozen_and_keep_every_material_knob()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out _));
        var draw = Assert.Single(binding.PreparedEquipmentDraws.ToArray()).Rigid;
        Assert.NotEqual(default, draw.Coverage);
        Assert.Equal(CoverageBindingFixture.Template.Tint, draw.Tint);
        Assert.Equal(CoverageBindingFixture.Template.Material, draw.Material);
        Assert.Equal(CoverageBindingFixture.Template.Dissolve, draw.Dissolve);
        Assert.Equal(CoverageBindingFixture.Template.DissolveComplement, draw.DissolveComplement);
        Assert.Equal(CoverageBindingFixture.Template.DissolveEdgeColor, draw.DissolveEdgeColor);
        Assert.Equal(CoverageBindingFixture.Template.DissolveEdgeWidth, draw.DissolveEdgeWidth);
        Assert.Equal(CoverageBindingFixture.Template.InvertShadowDissolve, draw.InvertShadowDissolve);
        Assert.Throws<CoverageFrameStateException>(() => f.Scene.Draw(draw with { World = Matrix4x4.CreateTranslation(1, 0, 0) }));
        Assert.Empty(f.Scene.QueuedInstancesForTests);
        f.Scene.Draw(draw);
        Assert.Single(f.Scene.QueuedInstancesForTests);
    }
}
