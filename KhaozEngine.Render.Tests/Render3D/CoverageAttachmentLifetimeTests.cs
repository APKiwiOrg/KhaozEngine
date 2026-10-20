using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageAttachmentLifetimeTests
{
    [Fact]
    public void Mixed_equipment_palettes_are_owned_per_actor_until_next_preparation()
    {
        using var f = new CoverageAttachmentFixture();
        var rigid = f.Load();
        var skin = new SkinnedAttachmentGlbFixture(2);
        skin.Nodes[0]!["name"] = "root";
        skin.Nodes[1]!["name"] = "hip";
        skin.SetSkinJoints(0, 1);
        var mesh = f.Source.Mesh("garment.glb", skin.Build());
        string hash = f.Source.Add("mixed.json", AttachmentSetJson.Write(new AttachmentSetDefinition(
            [.. f.Loaded.Definition.Parts, new SkinnedAttachmentPartDefinition(2, mesh)])));
        var loaded = f.Cache.Get("mixed.json", hash);
        string companion = f.Source.Add("mixed-coverage.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(
            hash, f.MapHash, [f.Command()])));
        var bound = loaded.Bind(f.Map(f.Skeleton), f.HostSkin);
        var equipment = new CoveredAttachmentInstance(7, bound, f.Cache.GetCoverage(loaded, "mixed-coverage.json", companion));
        using var a = f.Binding(91);
        using var b = f.Binding(92);
        Assert.True(a.TrySetEquipment([equipment], out _));
        Assert.True(b.TrySetEquipment([equipment with { InstanceId = 8 }], out _));
        f.Scene.Begin();
        Assert.True(a.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var af, out var failure), failure.ToString());
        var ad = a.PreparedEquipmentDraws.ToArray();
        var bp = new[] { Matrix4x4.CreateTranslation(0, .1f, 0), Matrix4x4.CreateTranslation(0, .1f, 0) };
        var bm = Matrix4x4.CreateTranslation(4, 0, 0);
        Assert.True(b.TryPrepare(f.Skeleton, bp, bm, f.Template, out var bf, out failure), failure.ToString());
        var bd = b.PreparedEquipmentDraws.ToArray();
        bound.WriteDraws(f.Skeleton, [Matrix4x4.CreateScale(9), Matrix4x4.CreateScale(9)], Matrix4x4.Identity,
            MotionKey.From(5), f.Template, new AttachmentDraw[bound.DrawCount]);
        Assert.Equal(Matrix4x4.Identity, Assert.Single(ad, d => d.IsSkinned).BoneMatrices.Span[1]);
        Assert.Equal(bp[1], Assert.Single(bd, d => d.IsSkinned).BoneMatrices.Span[1]);
        f.Submit(af, ad);
        f.Submit(bf, bd, 92, bm, bp);
        f.Render();
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(af).Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(bf).Presentation);
        Assert.Same(rigid.Coverage.Supports[0].Source, equipment.Coverage.Supports[0].Source);
    }

    [Fact]
    public void Disposing_binding_retires_only_its_conformer_and_rejects_new_host_submission()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        int from = f.Factory.Buffers.Count;
        var binding = f.Binding();
        var conformerBuffers = f.Factory.Buffers.Skip(from).ToArray();
        Assert.NotEmpty(conformerBuffers);
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out _));
        binding.Dispose();
        binding.Dispose();
        Assert.Throws<InvalidOperationException>(() => f.Scene.DrawSkinned(
            new SkinnedInstanceDraw(f.Hosts[0], Matrix4x4.Identity) { Motion = MotionKey.From(91), Coverage = frame }, f.Palette));
        Assert.Empty(f.Scene.QueuedSkinnedInstancesForTests);
        f.DrainRetired();
        Assert.All(conformerBuffers, buffer => Assert.True(buffer.Disposed));
        equipment.Set.Queue(f.Skeleton, f.Palette, Matrix4x4.Identity, MotionKey.From(7));
        Assert.Equal(2, f.Scene.QueuedInstancesForTests.Count);
    }

    [Fact]
    public void Invalid_batch_leaves_registry_and_destination_unchanged()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        var draws = new RigidInstanceDraw[2];
        equipment.Set.WriteDraws(f.Skeleton, f.Palette, Matrix4x4.Identity, MotionKey.From(1), f.Template, draws);
        var sentinel = new RigidInstanceDraw(default, Matrix4x4.CreateTranslation(77, 0, 0));
        var frozen = new[] { sentinel, sentinel };
        var support = equipment.Coverage.Supports[0];
        f.Scene.Begin();
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7, [], draws, frozen, out var failure));
        Assert.Equal(CoverageFailureCode.CapacityExceeded, failure.Code);
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7,
            [new(1, support), new(2, support), new(3, support), new(4, support), new(5, support)], draws, frozen, out failure));
        Assert.Equal(5, failure.Count);
        Assert.Equal(4, failure.Limit);
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7,
            [new(1, support), new(1, support)], draws, frozen, out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.All(frozen, d => Assert.Equal(sentinel, d));
        var other = f.Load("other");
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7,
            [new(1, support), new(2, other.Coverage.Supports[0])], draws, frozen, out failure));
        Assert.All(frozen, d => Assert.Equal(sentinel, d));
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7, [new(1, support)],
            draws.AsSpan(0, 1), frozen.AsSpan(0, 1), out failure));
        Assert.True(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 1, 7, support, draws, frozen, out failure), failure.ToString());
        var accepted = frozen.ToArray();
        Assert.False(f.Scene.TryStageCoverageEquipment(MotionKey.From(91), 7,
            [new(2, support), new(1, support)], draws, frozen, out failure));
        Assert.Equal(accepted, frozen);
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Missing_physical_material_refuses_all_shared_commands_at_resolution()
    {
        using var f = new CoverageAttachmentFixture(secondRegion: true);
        var equipment = f.Load(twoCommands: true);
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out var failure), failure.ToString());
        f.Submit(frame, [binding.PreparedEquipmentDraws.Span[0]]);
        f.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, f.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(CoverageFailureCode.IdentityMismatch, f.Scene.GetCoverageOutcome(frame).Failure.Code);
    }

    [Fact]
    public void Warm_binding_preparation_uses_owned_scratch_without_allocation()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Adapter.RecordStages = false;
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out _, out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out _, out _));
        f.Scene.Begin();
        var template = f.Template;
        using var region = AllocationRegion.Start("warm binding preparation");
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, template, out _, out var failure);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        Assert.True(accepted, failure.ToString());
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void One_physical_part_registers_two_disjoint_commands_without_duplicate_draws()
    {
        using var f = new CoverageAttachmentFixture(secondRegion: true);
        var equipment = f.Load(twoCommands: true);
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out var failure), failure.ToString());
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out failure), failure.ToString());
        var draws = binding.PreparedEquipmentDraws.ToArray();
        Assert.Equal(2, draws.Length);
        Assert.Equal(2, f.Adapter.Commands.Count);
        Assert.Equal(2, f.Adapter.Commands.Select(c => c.Command).Distinct().Count());
        Assert.All(f.Adapter.Commands, c => Assert.Equal(equipment.InstanceId, c.EquipmentInstanceId));
        f.Submit(frame, draws);
        Assert.Equal(f.Hosts.Length + 2, f.Adapter.QueuedDrawCount);
        f.Render();
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(frame).Presentation);
        var staged = frame.Preparation!.Commands.AsSpan(0, frame.Preparation.CommandCount).ToArray();
        Assert.All(f.Adapter.Commands, c => Assert.Contains(staged, p => p.Id == c.Command
            && p.EquipmentInstanceId == equipment.InstanceId && ReferenceEquals(p.Support, c.Support)));
    }

    [Fact]
    public void Coverage_compiles_owned_verified_bytes_without_reading_or_uploading_again()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load(beforeCoverage: () =>
        {
            Array.Clear(f.Source.Bytes["plate.glb"]);
            Array.Clear(f.Source.Bytes["set.json"]);
        });
        int buffers = f.Factory.Buffers.Count;
        var again = f.Cache.GetCoverage(f.Loaded, "coverage.json", f.Hash);
        Assert.Same(equipment.Coverage, again);
        string secondHash = f.Companion("second.json", f.Command());
        var second = f.Cache.GetCoverage(f.Loaded, "second.json", secondHash);
        Assert.Same(equipment.Coverage.Supports[0].Source, second.Supports[0].Source);
        Assert.Equal(1, f.Source.Reads["plate.glb"]);
        Assert.Equal(1, f.Source.Reads["set.json"]);
        Assert.Equal(buffers, f.Factory.Buffers.Count);
        using var foreign = new AttachmentSetCache(f.Adapter, f.Source);
        Assert.Throws<ArgumentException>(() => foreign.GetCoverage(f.Loaded, "second.json", secondHash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_actors_share_cache_but_keep_immutable_pose_and_descriptor_snapshots(bool reverse)
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var a = f.Binding(91);
        using var b = f.Binding(92);
        Assert.True(a.TrySetEquipment([equipment], out _));
        Assert.True(b.TrySetEquipment([equipment with { InstanceId = ulong.MaxValue }], out _));
        f.Scene.Begin();
        var original = f.Palette.ToArray();
        Assert.True(a.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var af, out var failure), failure.ToString());
        var ad = a.PreparedEquipmentDraws.ToArray();
        var modelB = Matrix4x4.CreateTranslation(4, 0, 0);
        Assert.True(b.TryPrepare(f.Skeleton, f.Palette, modelB, f.Template, out var bf, out failure), failure.ToString());
        var bd = b.PreparedEquipmentDraws.ToArray();
        equipment.Set.WriteDraws(f.Skeleton, f.Palette, Matrix4x4.CreateTranslation(99, 0, 0),
            MotionKey.From(33), f.Template, new AttachmentDraw[2]);
        Array.Clear(f.Palette);
        Assert.All(ad, d => Assert.Equal(Matrix4x4.Identity, d.Rigid.World));
        Assert.All(bd, d => Assert.Equal(modelB, d.Rigid.World));
        if (reverse) { f.Submit(bf, bd, 92, modelB, original); f.Submit(af, ad, palette: original); }
        else { f.Submit(af, ad, palette: original); f.Submit(bf, bd, 92, modelB, original); }
        f.Render();
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(af).Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(bf).Presentation);
        Assert.Equal(2, f.Scene.DispatchedHostPreparationCount);
    }

    [Fact]
    public void Cache_and_binding_dispose_once_and_invalidate_borrowed_state()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Cache.Dispose();
        f.Cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => binding.TryPrepare(f.Skeleton, f.Palette,
            Matrix4x4.Identity, f.Template, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => equipment.Coverage.Supports);
        binding.Dispose();
        binding.Dispose();
        Assert.Throws<ObjectDisposedException>(() => binding.TrySetEquipment([], out _));
        Assert.Equal(0, f.Adapter.QueuedDrawCount);
    }

    [Fact]
    public void Tile_adapter_forwards_active_opaque_tokens_unchanged()
    {
        using var f = new CoverageAttachmentFixture(tileAdapter: true);
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out _));
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template,
            out var frame, out var failure), failure.ToString());
        f.Submit(frame, binding.PreparedEquipmentDraws.ToArray());
        f.Render();
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(frame).Presentation);
    }
}

internal sealed class CoverageAttachmentFixture : IDisposable
{
    readonly CoverageSceneFixture render = new(seams: true);
    internal Scene3D Scene => render.Scene;
    internal FakeGpuResourceFactory Factory => render.Factory;
    internal AttachmentTestSource Source { get; } = new();
    internal CoverageAttachmentScene Adapter { get; }
    internal AttachmentSetCache Cache { get; }
    internal LoadedAttachmentSet Loaded { get; private set; } = null!;
    internal Skeleton Skeleton { get; }
    internal SkinnedMeshHandle[] Hosts { get; }
    internal SkinnedGltfMesh HostSkin => new(render.HostMesh.Vertices, render.HostMesh.Indices32,
        render.HostMesh.InverseBind, render.HostMesh.RestPose, Skeleton);
    readonly HostCoverageMap hostMap;
    internal Matrix4x4[] Palette { get; } = [Matrix4x4.Identity, Matrix4x4.Identity];
    internal string Hash { get; private set; } = "";
    internal string MapHash => HostCoverageMapJson.SemanticSha256(hostMap);
    internal RigidInstanceDraw Template => new(default, Matrix4x4.Identity) { Tint = Color.White };

    internal CoverageAttachmentFixture(bool tileAdapter = false, bool secondRegion = false)
    {
        Skeleton = CopySkeleton();
        Hosts = render.Host;
        hostMap = render.Conformer.Topology.Map;
        if (secondRegion)
        {
            var data = CoverageFixture.CurvedPanel(seams: false, secondRegion: true);
            hostMap = data.Map;
            Hosts = data.Source.Materials.Select(material =>
            {
                var vertices = data.Source.Vertices.Select((v, i) => (v, i)).Where(p => p.v.MaterialSlice == material.Slice)
                    .OrderBy(p => p.v.SliceVertex).Select(p => data.Source.SkinVertices[p.i]).ToArray();
                var indices = data.Source.Triangles.Where(t => t.MaterialSlice == material.Slice).SelectMany(t =>
                    new[] { (uint)data.Source.Vertices[t.A].SliceVertex, (uint)data.Source.Vertices[t.B].SliceVertex,
                        (uint)data.Source.Vertices[t.C].SliceVertex }).ToArray();
                var identity = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
                var mesh = new SkinnedGltfMesh(vertices, indices, identity, identity, Skeleton);
                return Scene.LoadSkinnedMesh(new SkinnedGltfMesh(vertices, indices, identity, identity, Skeleton)
                { Coverage = HostCoveragePart.Create(hostMap, data.Source, material.Slice, mesh) });
            }).ToArray();
        }
        Adapter = new CoverageAttachmentScene(tileAdapter ? new Scene3DTileWorldScene(Scene) : new Scene3DAttachmentScene(Scene));
        Cache = new AttachmentSetCache(Adapter, Source);
    }
    internal Skeleton CopySkeleton()
    {
        var s = render.HostMesh.Skeleton!;
        return new((int[])s.ParentIndices.Clone(), (JointPose[])s.RestLocal.Clone(),
            (int[])s.NodeLogicalIndex.Clone(), (int[])s.JointToNode.Clone(), ["root", "hip"]);
    }
    internal ContractJointMap Map(Skeleton skeleton) => new(skeleton,
        new SkeletonContract([new("root", null, true), new("hip", "root", true)]));
    internal HostCoverageBinding Binding(ulong actor = 91) => new(Adapter, Skeleton, Hosts, MotionKey.From(actor),
        CoverageFallback.UncoveredCurrentPose);
    internal CoveredAttachmentInstance Load(string prefix = "", string region = "r0", string? mapHash = null, Matrix4x4? local = null,
        bool twoCommands = false, Action? beforeCoverage = null)
    {
        var mesh = Source.Mesh(prefix + "plate.glb", SolidGlb(twoCommands));
        var definition = new AttachmentSetDefinition([new RigidAttachmentPartDefinition(1, mesh, "root", local ?? Matrix4x4.Identity)]);
        string hash = Source.Add(prefix + "set.json", AttachmentSetJson.Write(definition));
        Loaded = Cache.Get(prefix + "set.json", hash);
        beforeCoverage?.Invoke();
        var commands = new List<EquipmentCoverageCommand> { Command(region: region) };
        if (twoCommands) commands.Add(new(2, 1, "r1", Matrix4x4.CreateTranslation(4, 0, 0),
            [new(0, 0, 4, 4)], .4f, -2, 2, .02f, .8f));
        Hash = Source.Add(prefix + "coverage.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(hash,
            mapHash ?? MapHash, commands)));
        var coverage = Cache.GetCoverage(Loaded, prefix + "coverage.json", Hash);
        return new(ulong.MaxValue - 1, Loaded.Bind(Map(Skeleton)), coverage, 1);
    }
    internal EquipmentCoverageCommand Command(uint part = 1, string region = "r0") => new(1, part, region,
        Matrix4x4.Identity, [new(0, 0, 0, 4)], .4f, -2, 2, .02f, .8f);
    internal string Companion(string key, EquipmentCoverageCommand command) => Source.Add(key,
        EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(Loaded.DefinitionSha256, MapHash, [command])));
    internal void Submit(SkinnedCoverageFrame frame, ReadOnlySpan<AttachmentDraw> equipment, ulong actor = 91,
        Matrix4x4? model = null, Matrix4x4[]? palette = null)
    {
        var group = Scene.BeginMeshOutline(Color.White, 1);
        foreach (var handle in Hosts)
        {
            var draw = new SkinnedInstanceDraw(handle, model ?? Matrix4x4.Identity) { Motion = MotionKey.From(actor), Coverage = frame };
            Adapter.DrawSkinned(draw, palette ?? Palette);
            Adapter.DrawSkinnedOutline(group, draw, palette ?? Palette);
        }
        foreach (var draw in equipment)
        {
            if (draw.IsSkinned)
            {
                Adapter.DrawSkinned(draw.Skinned, draw.BoneMatrices.Span);
                Adapter.DrawSkinnedOutline(group, draw.Skinned, draw.BoneMatrices.Span);
            }
            else
            {
                Adapter.DrawMesh(draw.Rigid);
                Adapter.DrawMeshOutline(group, draw.Rigid);
            }
        }
    }
    internal void Render(bool prepare = true) => render.Render(prepare: prepare);
    internal void RenderUncaptured(IGpuCommandList commands) => render.RenderUncaptured(commands);
    internal RecordingGpuCommandList Record => render.Record;
    internal void DrainRetired()
    {
        for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) Scene.Begin();
    }
    static byte[] SolidGlb(bool secondRegion)
    {
        var data = CoverageFixture.CurvedPanel();
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("plate");
        var inward = mesh.UsePrimitive(new MaterialBuilder("inward"));
        var rest = mesh.UsePrimitive(new MaterialBuilder("solid"));
        for (int plate = 0; plate < (secondRegion ? 2 : 1); plate++)
            for (int t = 0; t < data.RigidIndices.Length / 3; t++)
            {
                VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(int c) =>
                    new(new VertexPositionNormal(data.RigidPositions[data.RigidIndices[t * 3 + c]] + new Vector3(plate * 4, 0, 0), Vector3.UnitZ));
                (t < 4 ? inward : rest).AddTriangle(V(0), V(1), V(2));
            }
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string path = Path.Combine(Path.GetTempPath(), $"coverage-attachment-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }
    public void Dispose() { Cache.Dispose(); render.Dispose(); }
}

internal sealed class CoverageAttachmentScene(IAttachmentCoverageScene scene) : IAttachmentCoverageScene
{
    internal object? OwnerOverride { get; set; }
    internal bool NullOwner { get; set; }
    public object CoverageResourceOwner => NullOwner ? null! : OwnerOverride ?? scene.CoverageResourceOwner;
    internal bool RecordStages { get; set; } = true;
    internal int QueuedDrawCount { get; private set; }
    internal List<(uint Command, ulong EquipmentInstanceId, CoverageSupportSurface Support)> Commands { get; } = [];
    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => scene.UploadAttachment(parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) => scene.UploadSkinnedAttachment(parts);
    public SkinnedHostConformer CreateHostConformer(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> hosts) => scene.CreateHostConformer(skeleton, hosts);
    public bool TryStageCoverageEquipment(MotionKey actor, ulong instance, ReadOnlySpan<CoverageEquipmentCommand> commands,
        ReadOnlySpan<RigidInstanceDraw> draws, Span<RigidInstanceDraw> frozen, out CoverageFailure failure)
    {
        bool result = scene.TryStageCoverageEquipment(actor, instance, commands, draws, frozen, out failure);
        if (result && RecordStages) foreach (var command in commands) Commands.Add((command.CommandId, instance, command.Support));
        return result;
    }
    public bool TryStageCoverageEquipment(MotionKey actor, uint command, ulong instance, CoverageSupportSurface support,
        ReadOnlySpan<RigidInstanceDraw> draws, Span<RigidInstanceDraw> frozen, out CoverageFailure failure)
    {
        bool result = scene.TryStageCoverageEquipment(actor, command, instance, support, draws, frozen, out failure);
        if (result && RecordStages) Commands.Add((command, instance, support));
        return result;
    }
    public void DrawMesh(in RigidInstanceDraw draw) { scene.DrawMesh(draw); QueuedDrawCount++; }
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette) { scene.DrawSkinned(draw, palette); QueuedDrawCount++; }
    public void DrawMeshOutline(MeshOutlineGroup group, in RigidInstanceDraw draw) => scene.DrawMeshOutline(group, draw);
    public void DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette) => scene.DrawSkinnedOutline(group, draw, palette);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement) =>
        scene.DrawMeshOutlineDissolved(group, mesh, world, dissolve, complement);
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh, ReadOnlySpan<Matrix4x4> palette,
        Matrix4x4 world, float dissolve, bool complement) => scene.DrawSkinnedOutlineDissolved(group, mesh, palette, world, dissolve, complement);
}
