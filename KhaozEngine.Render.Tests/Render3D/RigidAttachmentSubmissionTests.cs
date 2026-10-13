using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class RigidAttachmentSubmissionTests
{
    static readonly MotionKey Key = MotionKey.From(123);
    static readonly Matrix4x4 Model = Matrix4x4.CreateTranslation(10, 0, 0);
    static readonly RigidInstanceDraw Template = new(new MeshHandle(999, 2), Matrix4x4.CreateTranslation(99, 0, 0))
    {
        Tint = new Color(.4f, .5f, .6f, .7f),
        Material = new Material(new Color(1, 0, 0, 1), .6f, 48f),
        Dissolve = .3f,
        DissolveEdgeWidth = .12f,
        DissolveEdgeColor = new Color(0, 0, 1, 1),
        CastsShadows = false,
        InvertShadowDissolve = true,
        DissolveComplement = 1f,
        Motion = MotionKey.From(999),
    };

    [Fact]
    public void Material_subdraws_have_distinct_temporal_keys_and_preserve_every_template_field()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load(2);
        var draws = new RigidInstanceDraw[3];
        draws[2] = Template;

        Assert.Equal(2, bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, draws));

        Assert.NotEqual(draws[0].Mesh, draws[1].Mesh);
        Assert.NotEqual(draws[0].Motion, draws[1].Motion);
        Assert.All(draws.Take(2), draw => Assert.False(draw.Motion.IsNone));
        Assert.All(draws.Take(2), draw => Assert.False(draw.CastsShadows));
        Assert.Equal(new Vector3(11, 2, 0), draws[0].World.Translation);
        foreach (PropertyInfo property in typeof(RigidInstanceDraw).GetProperties())
        {
            if (property.Name is nameof(RigidInstanceDraw.Mesh) or nameof(RigidInstanceDraw.World) or nameof(RigidInstanceDraw.Motion)) continue;
            Assert.Equal(property.GetValue(Template), property.GetValue(draws[0]));
        }
        Assert.Equal(Template, draws[2]);
        bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key, Template);
        Assert.Equal(draws.Take(2), fixture.Capability.Draws.Take(fixture.Capability.Count));

        RigidInstanceDraw shadowTemplate = Template with { CastsShadows = true, ShadowOnly = true };
        bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, shadowTemplate, draws);
        Assert.All(draws.Take(2), draw => Assert.True(draw.ShadowOnly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packed_instance_uses_the_independent_literal_world_point(bool posed)
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load(2, twoParts: true);
        if (posed) RigidAttachmentTestRig.RotatedPalette(fixture.Skeleton).CopyTo(fixture.Palette, 0);
        var draws = new RigidInstanceDraw[bound.DrawCount];
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, template, draws);
        fixture.Rig.Scene.Camera.Frame(new Vector3(10, 2, 0), new Vector3(6, 4, 4));
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };

        fixture.Rig.Frame(_ => bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key), commands);

        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        Scene3D.GroupInstances(fixture.Rig.Scene.QueuedInstancesForTests, data, runs);
        RecordingGpuCommandList.Upload upload = Assert.Single(commands.Uploads,
            item => item.Bytes == bound.DrawCount * ModelRenderer.InstanceData.SizeInBytes);
        ModelRenderer.InstanceData[] packed = MemoryMarshal.Cast<byte, ModelRenderer.InstanceData>(upload.Data!).ToArray();
        Scene3D.MeshRun first = Assert.Single(runs, run => run.Mesh.Equals(draws[0].Mesh));
        Assert.Equal(posed ? new Vector3(10, 3, 0) : new Vector3(11, 2, 0),
            Vector3.Transform(Vector3.Zero, packed[first.Start].Model));
        Assert.Equal(posed ? new Vector3(11, 2, 0) : new Vector3(10, 1, 0), packed[first.Start + 1].Model.Translation);
        Assert.Equal(Vector4.One, packed[first.Start].Tint);

        fixture.Capability.Clear();
        fixture.Rig.Scene.Begin();
        MeshOutlineGroup outline = fixture.Rig.Scene.BeginMeshOutline(new Color(1, 0, 0, 1), 2);
        foreach (RigidInstanceDraw draw in draws)
            fixture.Rig.Scene.DrawMeshOutline(outline, draw.Mesh, draw.World);
        fixture.Rig.Scene.PrepareFrame();
        fixture.Rig.Render();
    }

    [Fact]
    public void Source_reordering_retains_keys_and_mesh_replacement_or_caller_replacement_changes_them()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet first = fixture.Load(2, twoParts: true);
        var original = new RigidInstanceDraw[first.DrawCount];
        first.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, original);
        RigidAttachmentPartDefinition[] parts = fixture.Loaded!.Definition.Parts.Reverse().ToArray();
        string originalHash = fixture.Loaded!.DefinitionSha256;
        BoundRigidAttachmentSet reordered = fixture.LoadDefinition("reordered.json", parts, reorderSource: true);
        Assert.NotEqual(originalHash, fixture.Loaded!.DefinitionSha256);
        var reorderedDraws = new RigidInstanceDraw[first.DrawCount];
        reordered.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, reorderedDraws);
        Assert.Equal(original.Select(draw => draw.Motion), reorderedDraws.Select(draw => draw.Motion));
        Assert.Equal(original.Select(draw => draw.Mesh), reorderedDraws.Select(draw => draw.Mesh));

        first.WriteDraws(fixture.Skeleton, fixture.Palette, Model, MotionKey.From(124), Template, reorderedDraws);
        Assert.All(Enumerable.Range(0, original.Length), i => Assert.NotEqual(original[i].Motion, reorderedDraws[i].Motion));
        AttachmentMeshRef replacement = fixture.Source.Mesh("parts/rigid.glb", AttachmentTestSource.Glb(3));
        BoundRigidAttachmentSet changed = fixture.LoadDefinition("changed.json",
            parts.Select(part => part with { Mesh = replacement }).ToArray());
        var changedDraws = new RigidInstanceDraw[changed.DrawCount];
        changed.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, changedDraws);
        Assert.NotEqual(original[0].Motion, changedDraws[0].Motion);
        changed.WriteDraws(fixture.Skeleton, fixture.Palette, Model, MotionKey.None, Template, changedDraws);
        Assert.All(changedDraws, draw => Assert.True(draw.Motion.IsNone));
    }

    [Fact]
    public void Insufficient_capacity_leaves_every_descriptor_untouched()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load(2);
        RigidInstanceDraw[] shortBuffer = [Template];
        Assert.Throws<ArgumentException>(() => bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, shortBuffer));
        Assert.Equal(Template, shortBuffer[0]);
    }

    [Fact]
    public void Invalid_late_pose_queues_no_prefix_and_writes_no_descriptors()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load(2, twoParts: true);
        fixture.Palette[0].M41 = float.NaN;
        RigidInstanceDraw[] draws = Enumerable.Repeat(Template, bound.DrawCount).ToArray();
        Assert.Throws<ArgumentException>(() => bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key, Template));
        Assert.Equal(0, fixture.Capability.Count);
        Assert.Empty(fixture.Rig.Scene.QueuedInstancesForTests);
        Assert.Throws<ArgumentException>(() => bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, draws));
        Assert.All(draws, draw => Assert.Equal(Template, draw));
    }

    [Fact]
    public void Default_queue_draws_opaque_white_and_casts_shadows()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load();
        bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key);
        RigidInstanceDraw draw = fixture.Capability.Draws[0];
        Assert.Equal(Color.White, draw.Tint);
        Assert.Equal(Material.None, draw.Material);
        Assert.Equal(0f, draw.Dissolve);
        Assert.True(draw.CastsShadows);
        Assert.False(draw.ShadowOnly);
        Assert.False(draw.InvertShadowDissolve);
        Assert.Equal(0f, draw.DissolveComplement);
        Assert.False(draw.Motion.IsNone);
        Assert.Equal(Color.White, Assert.Single(fixture.Rig.Scene.QueuedInstancesForTests).Tint);
    }

    [Fact]
    public void Disposed_cache_refuses_both_queue_overloads_and_descriptor_writing()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet bound = fixture.Load();
        var draws = new RigidInstanceDraw[1];
        fixture.Cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key));
        Assert.Throws<ObjectDisposedException>(() => bound.Queue(fixture.Skeleton, fixture.Palette, Model, Key, Template));
        Assert.Throws<ObjectDisposedException>(() => bound.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, draws));
        Assert.Equal(0, fixture.Capability.Count);
    }

    [Fact]
    public void First_submission_and_descriptor_writing_allocate_no_frame_memory()
    {
        using var fixture = new SubmissionFixture();
        BoundRigidAttachmentSet warm = fixture.Load(2);
        var draws = new RigidInstanceDraw[2];
        warm.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, draws);
        warm.Queue(fixture.Skeleton, fixture.Palette, Model, Key, Template);
        BoundRigidAttachmentSet fresh = fixture.Loaded!.Bind(RigidAttachmentTestRig.Map(fixture.Skeleton));
        fixture.Rig.Scene.Begin();
        fixture.Capability.Clear();
        fixture.Source.OnRead = _ => throw new InvalidOperationException("Frame submission cannot read source assets.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        fresh.WriteDraws(fixture.Skeleton, fixture.Palette, Model, Key, Template, draws);
        fresh.Queue(fixture.Skeleton, fixture.Palette, Model, Key, Template);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.Equal(2, fixture.Capability.Count);
    }

    sealed class SubmissionFixture : IDisposable
    {
        internal readonly HeadlessSceneRig Rig = new();
        internal readonly AttachmentTestSource Source = new();
        internal readonly Skeleton Skeleton = RigidAttachmentTestRig.Create();
        internal readonly Matrix4x4[] Palette;
        internal readonly SubmissionScene Capability;
        internal readonly RigidAttachmentSetCache Cache;
        internal LoadedRigidAttachmentSet? Loaded;
        internal SubmissionFixture()
        {
            Palette = Skeleton.ComposeRestPose();
            Capability = new SubmissionScene(Rig.Scene);
            Cache = new RigidAttachmentSetCache(Capability, Source);
        }
        internal BoundRigidAttachmentSet Load(int materials = 1, bool twoParts = false)
        {
            AttachmentMeshRef mesh = Source.Mesh("parts/rigid.glb", AttachmentTestSource.Glb(materials));
            RigidAttachmentPartDefinition first = new(1, mesh, "hip", Matrix4x4.CreateTranslation(1, 0, 0));
            RigidAttachmentPartDefinition second = new(2, mesh, "knee", Matrix4x4.Identity);
            return LoadDefinition("set.json", twoParts ? [first, second] : [first]);
        }
        internal BoundRigidAttachmentSet LoadDefinition(string key, RigidAttachmentPartDefinition[] parts, bool reorderSource = false)
        {
            byte[] bytes = RigidAttachmentSetJson.Write(new RigidAttachmentSetDefinition(parts));
            if (reorderSource)
            {
                JsonObject json = JsonNode.Parse(bytes)!.AsObject();
                json["parts"] = new JsonArray(json["parts"]!.AsArray().Reverse().Select(part => part!.DeepClone()).ToArray());
                bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
            }
            string hash = Source.Add(key, bytes);
            Loaded = Cache.Get(key, hash);
            return Loaded.Bind(RigidAttachmentTestRig.Map(Skeleton));
        }
        public void Dispose() { Cache.Dispose(); Rig.Dispose(); }
    }

    sealed class SubmissionScene(Scene3D scene) : IRigidAttachmentScene
    {
        readonly Scene3DAttachmentScene _adapter = new(scene);
        internal readonly RigidInstanceDraw[] Draws = new RigidInstanceDraw[32];
        internal int Count;
        public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => _adapter.UploadAttachment(parts);
        public void DrawMesh(in RigidInstanceDraw draw)
        {
            Draws[Count++] = draw;
            _adapter.DrawMesh(draw);
        }
        internal void Clear() => Count = 0;
    }
}
