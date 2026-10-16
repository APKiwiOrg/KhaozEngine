using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class SkinnedAttachmentSubmissionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Mixed_template_fields_and_palette_match_scene_submission(bool castsShadows, bool shadowOnly)
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        BoundAttachmentSet bound = fixture.Bound;
        RigidInstanceDraw template = MixedAttachmentSubmissionFixture.Template with
        { CastsShadows = castsShadows, ShadowOnly = shadowOnly };
        var draws = new AttachmentDraw[4];
        Assert.Equal(3, bound.DrawCount);
        Assert.Equal(3, bound.WriteDraws(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, template, draws));
        Assert.Equal(default, draws[3]);
        Assert.Equal(1u, draws[0].PartId);
        Assert.False(draws[0].IsSkinned);
        Assert.True(draws[0].BoneMatrices.IsEmpty);
        Assert.Equal(2u, draws[1].PartId);
        Assert.True(draws[1].IsSkinned);
        Assert.Equal(0, draws[1].MaterialIndex);
        Assert.Equal(1, draws[2].MaterialIndex);
        Assert.Equal(fixture.Model, draws[1].Skinned.Model);
        Assert.Equal(draws[1].BoneMatrices, draws[2].BoneMatrices);
        Assert.Equal(fixture.Palette[1], draws[1].BoneMatrices.Span[0]);
        Assert.Equal(fixture.Palette[0], draws[1].BoneMatrices.Span[1]);
        Assert.Equal(new Vector3(11, 2, 0), draws[0].Rigid.World.Translation);
        foreach (PropertyInfo property in typeof(RigidInstanceDraw).GetProperties())
        {
            if (property.Name is nameof(RigidInstanceDraw.Mesh) or nameof(RigidInstanceDraw.World) or nameof(RigidInstanceDraw.Motion)) continue;
            Assert.Equal(property.GetValue(template), property.GetValue(draws[0].Rigid));
            if (property.Name != nameof(RigidInstanceDraw.Coverage))
            {
                Assert.Equal(property.GetValue(template), typeof(SkinnedInstanceDraw).GetProperty(property.Name)!.GetValue(draws[1].Skinned));
            }
        }
        Assert.Equal(default(CoverageEquipmentDraw), draws[0].Rigid.Coverage);
        Assert.Equal(default(SkinnedCoverageFrame), draws[1].Skinned.Coverage);
        Assert.Equal(default(SkinnedCoverageFrame), draws[2].Skinned.Coverage);

        bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, template);

        Assert.Equal(1, fixture.Capability.RigidCalls);
        Assert.Equal(2, fixture.Capability.SkinnedCalls);
        Assert.Equal(draws[0].Rigid, fixture.Capability.RigidDraw);
        Assert.Equal(draws[1].Skinned, fixture.Capability.SkinnedDraws[0]);
        Assert.Equal(draws[2].Skinned, fixture.Capability.SkinnedDraws[1]);
        Assert.Equal(fixture.Palette[1], fixture.Capability.Palettes[0][0]);
        Assert.Equal(fixture.Palette[0], fixture.Capability.Palettes[0][1]);
        Assert.Equal(draws[0].Rigid.World, Assert.Single(fixture.Scene.QueuedInstancesForTests).World);
        for (int i = 0; i < 2; i++)
        {
            AssertQueuedSkin(draws[i + 1].Skinned, fixture.Scene.QueuedSkinnedInstancesForTests[i]);
            Matrix4x4[] actual = fixture.Scene.BonePaletteSlotForTests(i);
            Assert.Equal(Matrix4x4.CreateTranslation(0, 1, 0), actual[0]);
            Assert.Equal(Matrix4x4.CreateTranslation(0, -1, 0), actual[1]);
        }
    }

    [Fact]
    public void Mixed_material_motion_keys_are_stable_and_distinct()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        AttachmentDraw[] original = fixture.Write(fixture.Bound);
        MotionKey[] keys = original.Select(KeyOf).ToArray();
        Assert.Equal(3, keys.Distinct().Count());
        byte[] revisionBytes = Convert.FromHexString(fixture.Loaded.Definition.SemanticSha256[..16]);
        MotionKey revision = MotionKey.Combine(MotionKey.Combine(fixture.Key,
            BinaryPrimitives.ReadUInt32LittleEndian(revisionBytes)), BinaryPrimitives.ReadUInt32LittleEndian(revisionBytes.AsSpan(4)));
        Assert.Equal(MotionKey.Combine(MotionKey.Combine(revision, 2), 1), keys[2]);
        byte[] jsonBytes = AttachmentSetJson.Write(fixture.Loaded.Definition);
        JsonObject json = JsonNode.Parse(jsonBytes)!.AsObject();
        json["parts"] = new JsonArray(json["parts"]!.AsArray().Reverse().Select(part => part!.DeepClone()).ToArray());
        string hash = fixture.Source.Add("reordered.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        BoundAttachmentSet reordered = fixture.Cache.Get("reordered.json", hash).Bind(fixture.Map, fixture.Host);
        Assert.Equal(keys, fixture.Write(reordered).Select(KeyOf));
        Assert.All(fixture.Write(fixture.Bound, MotionKey.From(124)).Select(KeyOf), k => Assert.DoesNotContain(k, keys));
        Assert.All(fixture.Write(fixture.Bound, MotionKey.None).Select(KeyOf), k => Assert.True(k.IsNone));

        AttachmentMeshRef changedMesh = fixture.Source.Mesh("revised.glb", AttachmentTestSource.Glb());
        AttachmentPartDefinition[] changedParts = fixture.Loaded.Definition.Parts.Select(part =>
            part is RigidAttachmentPartDefinition rigid ? rigid with { Mesh = changedMesh } : part).ToArray();
        hash = fixture.Source.Add("changed.json", AttachmentSetJson.Write(new AttachmentSetDefinition(changedParts)));
        BoundAttachmentSet changed = fixture.Cache.Get("changed.json", hash).Bind(fixture.Map, fixture.Host);
        Assert.All(fixture.Write(changed).Select(KeyOf), k => Assert.DoesNotContain(k, keys));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_later_part_publishes_no_prefix(bool rigidLast)
    {
        using var fixture = new MixedAttachmentSubmissionFixture(rigidLast: rigidLast);
        AttachmentDraw[] draws = fixture.Write(fixture.Bound);
        AttachmentDraw[] before = (AttachmentDraw[])draws.Clone();
        fixture.Palette[0].M41 = float.NaN;

        Assert.Throws<ArgumentException>(() => fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template, draws));
        Assert.Equal(before, draws);
        Assert.Throws<ArgumentException>(() => fixture.Bound.Queue(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template));
        fixture.AssertNoCalls();
    }

    [Fact]
    public void Invalid_capacity_and_shadow_template_leave_output_and_scene_untouched()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        AttachmentDraw[] shortBuffer = fixture.Write(fixture.Bound)[..2];
        AttachmentDraw[] beforeShort = (AttachmentDraw[])shortBuffer.Clone();
        Assert.Throws<ArgumentException>(() => fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template, shortBuffer));
        Assert.Equal(beforeShort, shortBuffer);
        AttachmentDraw[] draws = fixture.Write(fixture.Bound);
        AttachmentDraw[] before = (AttachmentDraw[])draws.Clone();
        RigidInstanceDraw invalid = MixedAttachmentSubmissionFixture.Template with { ShadowOnly = true, CastsShadows = false };
        Assert.Throws<ArgumentException>(() => fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, invalid, draws));
        Assert.Equal(before, draws);
        Assert.Throws<ArgumentException>(() => fixture.Bound.Queue(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, invalid));
        fixture.AssertNoCalls();
    }

    [Fact]
    public void Mixed_sets_refuse_rigid_descriptors_and_map_only_binding()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        Assert.Throws<InvalidDataException>(() => fixture.Loaded.Bind(fixture.Map));
        RigidInstanceDraw[] draws = Enumerable.Repeat(MixedAttachmentSubmissionFixture.Template, 3).ToArray();
        Assert.Throws<InvalidOperationException>(() => fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template, draws));
        Assert.All(draws, draw => Assert.Equal(MixedAttachmentSubmissionFixture.Template, draw));
    }

    [Fact]
    public void Default_mixed_queue_uses_plain_draw_defaults()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key);
        RigidInstanceDraw rigid = fixture.Capability.RigidDraw;
        Assert.Equal(Color.White, rigid.Tint);
        Assert.Equal(Material.None, rigid.Material);
        Assert.Equal(0f, rigid.Dissolve);
        Assert.True(rigid.CastsShadows);
        foreach (SkinnedInstanceDraw draw in fixture.Capability.SkinnedDraws)
        {
            Assert.Equal(Color.White, draw.Tint);
            Assert.Equal(Material.None, draw.Material);
            Assert.Equal(0f, draw.Dissolve);
            Assert.True(draw.CastsShadows);
            Assert.False(draw.ShadowOnly);
            Assert.False(draw.InvertShadowDissolve);
            Assert.Equal(0f, draw.DissolveComplement);
        }
    }

    [Fact]
    public void Next_pose_does_not_overwrite_scene_or_previous_motion_palettes()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        fixture.Scene.ForceTemporalForTests = true;
        fixture.Scene.Begin();
        AttachmentDraw[] draws = fixture.Write(fixture.Bound);
        MotionKey key = draws[1].Skinned.Motion;
        fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key);
        Matrix4x4[] first = fixture.Scene.BonePaletteSlotForTests(0);
        fixture.Palette[1] = Matrix4x4.CreateTranslation(3, 4, 5);
        fixture.Write(fixture.Bound);
        Assert.Equal(first, fixture.Scene.BonePaletteSlotForTests(0));
        fixture.Scene.Begin();
        fixture.Capability.Clear();
        fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key);

        Assert.True(fixture.Scene.ActiveMotionHistory!.TryGetPreviousSkinned(key, out Matrix4x4 world, out ReadOnlySpan<Matrix4x4> previous));
        Assert.Equal(fixture.Model, world);
        Assert.Equal(first.AsSpan(0, 2).ToArray(), previous.ToArray());
        Assert.Equal(Matrix4x4.CreateTranslation(3, 3, 5), fixture.Scene.BonePaletteSlotForTests(0)[0]);
    }

    [Fact]
    public void Disposed_cache_refuses_every_mixed_submission()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        var draws = new AttachmentDraw[3];
        fixture.Cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template, draws));
        Assert.Throws<ObjectDisposedException>(() => fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key));
        Assert.Throws<ObjectDisposedException>(() => fixture.Bound.Queue(fixture.Skeleton, fixture.Palette,
            fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template));
        fixture.AssertNoCalls();
    }

    [Fact]
    public void Steady_mixed_submission_allocates_nothing()
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        fixture.Scene.ForceTemporalForTests = true;
        var draws = new AttachmentDraw[3];
        for (int frame = 0; frame < 3; frame++)
        {
            fixture.Scene.Begin();
            fixture.Capability.Clear();
            fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key,
                MixedAttachmentSubmissionFixture.Template, draws);
            fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template);
        }
        BoundAttachmentSet fresh = fixture.Loaded.Bind(fixture.Map, fixture.Host);
        fixture.Scene.Begin();
        fixture.Capability.Clear();
        fixture.Source.OnRead = _ => throw new InvalidOperationException("Frame submission cannot read source assets.");
        AllocAssert.NoPerCallAllocation("one warmed mixed submit", () =>
        {
            fresh.WriteDraws(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template, draws);
            fresh.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, MixedAttachmentSubmissionFixture.Template);
        });
        Assert.Equal(1, fixture.Capability.RigidCalls);
        Assert.Equal(2, fixture.Capability.SkinnedCalls);
        Assert.Equal((1, 2, 0), fixture.Scene.MotionKeyCounts);
    }

    static MotionKey KeyOf(AttachmentDraw draw) => draw.IsSkinned ? draw.Skinned.Motion : draw.Rigid.Motion;

    internal static void AssertQueuedSkin(SkinnedInstanceDraw draw, SkinnedSceneInstances.Instance actual)
    {
        Assert.Equal(draw.Mesh, actual.Mesh);
        Assert.Equal(draw.Model, actual.World);
        Assert.Equal((Vector4)draw.Tint, actual.Tint);
        Assert.Equal(draw.Material, actual.Material);
        Assert.Equal(draw.Dissolve, actual.DissolveThreshold);
        Assert.Equal(draw.DissolveEdgeWidth, actual.DissolveEdgeWidth);
        Assert.Equal((Vector4)draw.DissolveEdgeColor, actual.DissolveEdge);
        Assert.Equal(draw.CastsShadows, actual.CastsShadows);
        Assert.Equal(draw.ShadowOnly, actual.ShadowOnly);
        Assert.Equal(draw.InvertShadowDissolve, actual.InvertShadowDissolve);
        Assert.Equal(draw.DissolveComplement, actual.DissolveComplement);
        Assert.Equal(draw.Motion, actual.Motion);
    }
}

internal sealed class MixedAttachmentSubmissionFixture : IDisposable
{
    internal static readonly RigidInstanceDraw Template = new(default, Matrix4x4.Identity)
    {
        Tint = new Color(.4f, .5f, .6f, .7f),
        Material = new Material(new Color(1, 0, 0, 1), .6f, 48f),
        Dissolve = .3f,
        DissolveEdgeWidth = .12f,
        DissolveEdgeColor = new Color(0, 0, 1, 1),
        CastsShadows = true,
        InvertShadowDissolve = true,
        DissolveComplement = 1f,
    };
    readonly AttachmentAssetHarness _harness = new();
    internal Scene3D Scene => _harness.Scene;
    internal readonly AttachmentTestSource Source = new();
    internal readonly Skeleton Skeleton = RigidAttachmentTestRig.Create();
    internal readonly Matrix4x4[] Palette;
    internal readonly SkinnedGltfMesh Host;
    internal readonly ContractJointMap Map;
    internal readonly Matrix4x4 Model = Matrix4x4.CreateTranslation(10, 0, 0);
    internal readonly MotionKey Key = MotionKey.From(123);
    internal readonly MixedAttachmentScene Capability;
    internal readonly AttachmentSetCache Cache;
    internal readonly LoadedAttachmentSet Loaded;
    internal readonly BoundAttachmentSet Bound;

    internal MixedAttachmentSubmissionFixture(bool tileFacade = false, bool rigidLast = false)
    {
        Palette = Skeleton.ComposeRestPose();
        Map = RigidAttachmentTestRig.Map(Skeleton);
        Matrix4x4[] binds = [Matrix4x4.CreateTranslation(0, -2, 0), Matrix4x4.CreateTranslation(0, -1, 0)];
        Host = new SkinnedGltfMesh([], Array.Empty<ushort>(), binds, Palette, Skeleton);
        var glb = new SkinnedAttachmentGlbFixture(rigidLast ? 1 : 2);
        glb.Nodes[0]!["name"] = "hip";
        if (!rigidLast) glb.Nodes[1]!["name"] = "knee";
        glb.SetSkinJoints(rigidLast ? [0] : [0, 1]);
        glb.SetInverseBind(0, binds[1]);
        if (!rigidLast) glb.SetInverseBind(1, binds[0]);
        glb.Root["materials"] = new JsonArray(new JsonObject(), new JsonObject());
        glb.Primitives[0]!["material"] = 0;
        glb.Primitives.Add(glb.Primitives[0]!.DeepClone());
        glb.Primitives[1]!["material"] = 1;
        AttachmentMeshRef garment = Source.Mesh("garment.glb", glb.Build());
        AttachmentMeshRef rigid = Source.Mesh("rigid.glb", AttachmentTestSource.Glb());
        var definition = new AttachmentSetDefinition([
            new RigidAttachmentPartDefinition(rigidLast ? 2u : 1u, rigid, rigidLast ? "knee" : "hip", Matrix4x4.CreateTranslation(1, 0, 0)),
            new SkinnedAttachmentPartDefinition(rigidLast ? 1u : 2u, garment)]);
        string hash = Source.Add("mixed.json", AttachmentSetJson.Write(definition));
        Capability = new MixedAttachmentScene(tileFacade ? new Scene3DTileWorldScene(Scene) : new Scene3DAttachmentScene(Scene));
        Cache = new AttachmentSetCache(Capability, Source);
        Loaded = Cache.Get("mixed.json", hash);
        Bound = Loaded.Bind(Map, Host);
    }

    internal AttachmentDraw[] Write(BoundAttachmentSet bound, MotionKey? key = null)
    {
        var draws = new AttachmentDraw[bound.DrawCount];
        bound.WriteDraws(Skeleton, Palette, Model, key ?? Key, Template, draws);
        return draws;
    }

    internal void AssertNoCalls()
    {
        Assert.Equal(0, Capability.RigidCalls + Capability.SkinnedCalls + Capability.OutlineCalls);
        Assert.Empty(Scene.QueuedInstancesForTests);
        Assert.Empty(Scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(0, Scene.MeshOutlinePartCount);
        Assert.Equal(0, Scene.OutlinePoseMatrixCount);
    }

    public void Dispose() { Cache.Dispose(); _harness.Dispose(); }
}

internal sealed class MixedAttachmentScene(IAttachmentScene scene) : IAttachmentScene
{
    internal int RigidCalls, SkinnedCalls, OutlineCalls;
    internal RigidInstanceDraw RigidDraw;
    internal readonly SkinnedInstanceDraw[] SkinnedDraws = new SkinnedInstanceDraw[2];
    internal readonly Matrix4x4[][] Palettes = [new Matrix4x4[2], new Matrix4x4[2]];
    internal readonly float[] OutlineDissolves = new float[3];
    internal readonly bool[] OutlineComplements = new bool[3];
    internal readonly Matrix4x4[] OutlineWorlds = new Matrix4x4[3];
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => scene.UploadAttachment(parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) => scene.UploadSkinnedAttachment(parts);
    public void DrawMesh(in RigidInstanceDraw draw) { RigidDraw = draw; RigidCalls++; scene.DrawMesh(draw); }
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> bones)
    {
        SkinnedDraws[SkinnedCalls] = draw;
        bones.CopyTo(Palettes[SkinnedCalls++]);
        scene.DrawSkinned(draw, bones);
    }
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement)
    {
        RecordOutline(world, dissolve, complement);
        scene.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    }
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> bones, Matrix4x4 world, float dissolve, bool complement)
    {
        RecordOutline(world, dissolve, complement);
        scene.DrawSkinnedOutlineDissolved(group, mesh, bones, world, dissolve, complement);
    }
    void RecordOutline(Matrix4x4 world, float dissolve, bool complement)
    {
        OutlineWorlds[OutlineCalls] = world;
        OutlineDissolves[OutlineCalls] = dissolve;
        OutlineComplements[OutlineCalls++] = complement;
    }
    internal void Clear() { RigidCalls = 0; SkinnedCalls = 0; OutlineCalls = 0; }
}
