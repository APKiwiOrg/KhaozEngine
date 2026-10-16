using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageAttachmentBindingTests
{
    [Fact]
    public void Incomplete_host_materials_refuse_before_allocating_conformer_resources()
    {
        using var f = new CoverageAttachmentFixture();
        int buffers = f.Factory.Buffers.Count;
        Assert.Throws<ArgumentException>(() => new HostCoverageBinding(f.Adapter, f.Skeleton, [f.Hosts[0]],
            MotionKey.From(91), CoverageFallback.UncoveredCurrentPose));
        Assert.Equal(buffers, f.Factory.Buffers.Count);
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Skinned_target_and_partial_companion_load_leave_previous_equipment_available()
    {
        using var f = new CoverageAttachmentFixture();
        var previous = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([previous], out _));
        var mesh = f.Source.Mesh("garment.glb", new SkinnedAttachmentGlbFixture().Build());
        string hash = f.Source.Add("garment.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [new SkinnedAttachmentPartDefinition(7, mesh)])));
        var loaded = f.Cache.Get("garment.json", hash);
        string companionHash = f.Source.Add("garment-coverage.json", EquipmentCoverageJson.Write(
            new EquipmentCoverageDefinition(hash, f.MapHash, [f.Command(part: 7)])));
        int buffers = f.Factory.Buffers.Count;
        var error = Assert.Throws<System.IO.InvalidDataException>(() =>
            f.Cache.GetCoverage(loaded, "garment-coverage.json", companionHash));
        Assert.Contains("7", error.Message);
        string partial = f.Source.Add("partial.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(
            f.Loaded.DefinitionSha256, f.MapHash, [f.Command(), new EquipmentCoverageCommand(2, 1, "r1",
                Matrix4x4.Identity, [new(0, 0, 999, 1)], .4f, -2, 2, .02f, .8f)])));
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "partial.json", partial));
        Assert.Equal(buffers, f.Factory.Buffers.Count);
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out _, out var failure), failure.ToString());
        Assert.Equal(previous.InstanceId, Assert.Single(f.Adapter.Commands).EquipmentInstanceId);
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Combined_equipment_preserves_full_instance_ids_and_stable_distinct_commands()
    {
        using var f = new CoverageAttachmentFixture(secondRegion: true);
        var a = f.Load();
        var b = f.Load("other", region: "r1", local: Matrix4x4.CreateTranslation(4, 0, 0)) with { InstanceId = ulong.MaxValue };
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([a, b], out var failure), failure.ToString());
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out _, out failure), failure.ToString());
        var first = f.Adapter.Commands.ToDictionary(c => c.EquipmentInstanceId, c => c.Command);
        Assert.Equal(2, first.Values.Distinct().Count());
        Assert.Contains(ulong.MaxValue, first.Keys);
        Assert.Contains(ulong.MaxValue - 1, first.Keys);
        f.Adapter.Commands.Clear();
        Assert.True(binding.TrySetEquipment([b, a], out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out failure), failure.ToString());
        Assert.All(f.Adapter.Commands, c => Assert.Equal(first[c.EquipmentInstanceId], c.Command));
        f.Submit(frame, binding.PreparedEquipmentDraws.ToArray());
        f.Render();
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Preparation_composes_pose_and_model_once_and_preserves_complete_template()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        Matrix4x4 joint = Matrix4x4.CreateRotationZ(.2f) * Matrix4x4.CreateTranslation(.1f, .2f, 0);
        f.Palette[0] = f.Palette[1] = joint;
        var model = Matrix4x4.CreateScale(1.1f) * Matrix4x4.CreateRotationY(.1f) * Matrix4x4.CreateTranslation(1, 0, 0);
        var template = f.Template with
        {
            Tint = new(.8f, .7f, .6f, 1),
            Material = new(new(.1f, .2f, .3f, 1), .2f, 8),
            Dissolve = .1f,
            DissolveEdgeWidth = .01f,
            DissolveEdgeColor = new(.3f, .2f, .1f, 1),
            InvertShadowDissolve = true,
            DissolveComplement = 1,
        };
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, model, template, out var frame, out var failure), failure.ToString());
        var frozen = binding.PreparedEquipmentDraws.ToArray();
        Assert.All(frozen, d =>
        {
            var expected = joint * model;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Assert.InRange(MathF.Abs(expected[row, column] - d.Rigid.World[row, column]), 0, 1e-6f);
            Assert.Equal(template.Tint, d.Rigid.Tint);
            Assert.Equal(template.Material, d.Rigid.Material);
            Assert.Equal(template.Dissolve, d.Rigid.Dissolve);
            Assert.Equal(template.DissolveEdgeWidth, d.Rigid.DissolveEdgeWidth);
            Assert.Equal(template.DissolveEdgeColor, d.Rigid.DissolveEdgeColor);
            Assert.Equal(template.CastsShadows, d.Rigid.CastsShadows);
            Assert.Equal(template.ShadowOnly, d.Rigid.ShadowOnly);
            Assert.Equal(template.InvertShadowDissolve, d.Rigid.InvertShadowDissolve);
            Assert.Equal(template.DissolveComplement, d.Rigid.DissolveComplement);
        });
        f.Submit(frame, frozen, model: model);
        f.Render();
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Same_scene_wrappers_share_owner_but_colliding_foreign_handles_refuse()
    {
        using var f = new CoverageAttachmentFixture();
        using var foreign = new CoverageAttachmentFixture();
        var equipment = f.Load();
        foreign.Load();
        Assert.Equal(f.Hosts[0].Index, foreign.Hosts[0].Index);
        Assert.Equal(f.Hosts[0].Generation, foreign.Hosts[0].Generation);
        using var same = new HostCoverageBinding(new Scene3DAttachmentScene(f.Scene), f.Skeleton, f.Hosts,
            MotionKey.From(91), CoverageFallback.UncoveredCurrentPose);
        Assert.True(same.TrySetEquipment([equipment], out var failure), failure.ToString());
        using var different = new HostCoverageBinding(foreign.Adapter, f.Skeleton, foreign.Hosts,
            MotionKey.From(92), CoverageFallback.UncoveredCurrentPose);
        Assert.False(different.TrySetEquipment([equipment], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Empty(foreign.Adapter.Commands);
        Assert.Equal(0, foreign.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Invalid_opt_in_owner_refuses_requested_coverage()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        f.Adapter.OwnerOverride = new object();
        using var binding = f.Binding();
        Assert.False(binding.TrySetEquipment([equipment], out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        f.Adapter.OwnerOverride = null;
        Assert.False(binding.TrySetEquipment([equipment], out failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        Assert.Empty(f.Adapter.Commands);
        f.Adapter.NullOwner = true;
        using var nullOwner = f.Binding(93);
        Assert.False(nullOwner.TrySetEquipment([equipment], out failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
    }

    [Fact]
    public void Binding_requires_exact_host_and_equipment()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out var failure), failure.ToString());
        var other = f.Load("other", local: Matrix4x4.CreateTranslation(0, 0, .1f));
        Assert.False(binding.TrySetEquipment([equipment with { Set = other.Set }], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        var copy = f.CopySkeleton();
        Assert.False(binding.TrySetEquipment([equipment with { Set = f.Loaded.Bind(f.Map(copy)) }], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        f.Scene.Begin();
        Assert.False(binding.TryPrepare(copy, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out failure));
        Assert.Equal(default, frame);
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Empty(binding.PreparedEquipmentDraws.ToArray());
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Failed_replacement_preserves_active_equipment()
    {
        using var f = new CoverageAttachmentFixture();
        var previous = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([previous], out _));
        var replacement = f.Load("replacement", region: "absent");
        Assert.False(binding.TrySetEquipment([replacement], out var failure));
        Assert.Equal(CoverageFailureCode.UnknownRegion, failure.Code);
        Assert.False(binding.TrySetEquipment([previous, previous], out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.False(binding.TrySetEquipment([previous with { InstanceId = 0 }], out failure));
        Assert.False(binding.TrySetEquipment([previous with { TransitionWeight = float.NaN }], out failure));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out _, out failure), failure.ToString());
        Assert.Equal(previous.InstanceId, Assert.Single(f.Adapter.Commands).EquipmentInstanceId);
        Assert.Equal(2, binding.PreparedEquipmentDraws.Length);
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Legacy_adapter_rejects_only_requested_coverage()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        var legacy = new AttachmentRecordingScene(f.Scene);
        using var binding = new HostCoverageBinding(legacy, f.Skeleton, f.Hosts, MotionKey.From(91),
            CoverageFallback.UncoveredCurrentPose);
        Assert.True(binding.TrySetEquipment([], out _));
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out _));
        Assert.Equal(default, frame);
        Assert.False(binding.TrySetEquipment([equipment], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        using var legacyCache = new AttachmentSetCache(legacy, f.Source);
        var loaded = legacyCache.Get("set.json", f.Loaded.DefinitionSha256);
        loaded.Bind(f.Map(f.Skeleton)).Queue(f.Skeleton, f.Palette, Matrix4x4.Identity, MotionKey.From(7));
    }

    [Fact]
    public void Companion_hash_attachment_bytes_parts_and_support_ranges_are_checked()
    {
        using var f = new CoverageAttachmentFixture();
        f.Load();
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "missing.json", f.Hash));
        string stale = f.Source.Add("stale.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(
            new string('b', 64), f.MapHash, [f.Command()])));
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "stale.json", stale));
        string badPart = f.Companion("part.json", f.Command(part: 99));
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "part.json", badPart));
        string badRange = f.Companion("range.json", new EquipmentCoverageCommand(1, 1, "r0", Matrix4x4.Identity,
            [new(0, 0, 100, 1)], .4f, -2, 2, .02f, .8f));
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "range.json", badRange));
        string changed = f.Companion("changed.json", f.Command());
        f.Source.Bytes["changed.json"][0] ^= 1;
        Assert.Throws<System.IO.InvalidDataException>(() => f.Cache.GetCoverage(f.Loaded, "changed.json", changed));
        var staleMap = f.Load("stale-map", mapHash: new string('c', 64));
        using var binding = f.Binding();
        Assert.False(binding.TrySetEquipment([staleMap], out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Complete_frozen_materials_and_current_pose_reach_scene_and_outlines()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out var failure), failure.ToString());
        var frozen = binding.PreparedEquipmentDraws.ToArray();
        Assert.Equal(new[] { 0, 1 }, frozen.Select(d => d.MaterialIndex));
        Assert.All(frozen, d => Assert.NotEqual(default, d.Rigid.Coverage));
        Assert.All(frozen, d => Assert.Equal(f.Template.Tint, d.Rigid.Tint));
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
        f.Submit(frame, frozen);
        f.Render();
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(1, f.Scene.CoveredHostPreparationCount);
    }

    [Fact]
    public void Rejected_pose_returns_no_draw_or_token_and_unequip_uses_current_pose()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Scene.Begin();
        Assert.False(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.CreateScale(0), f.Template,
            out var frame, out _));
        Assert.Equal(default, frame);
        Assert.Empty(binding.PreparedEquipmentDraws.ToArray());
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
        Assert.True(binding.TrySetEquipment([], out _));
        f.Palette[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out frame, out _));
        Assert.Equal(default, frame);
        f.Submit(frame, []);
        f.Render();
        Assert.Equal(0, f.Scene.CoveredHostPreparationCount);
    }
}
