using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Cold-owned public staging inputs and independent reference outputs. Draw performs no test capture.</summary>
internal sealed class CoverageBatchActor : IDisposable
{
    readonly Scene3D scene;
    readonly CoverageFixture data;
    readonly SkinnedHostConformer conformer;
    readonly SkinnedMeshHandle[] hosts;
    readonly Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
    readonly Matrix4x4 world;
    readonly CoverageSupportSurface support;
    readonly CoverageCommandPose[] commands;
    readonly RigidInstanceDraw[][] inputs, frozen;
    internal readonly MotionKey Key;
    internal SkinnedCoverageFrame Frame;
    internal CoverageFailure StageFailure;
    internal bool Staged;
    internal CoverageRefinement Topology => conformer.Topology;
    internal CoverageFailure ExpectedFailure;
    internal uint[] ExpectedWords = [];
    internal CoverageGpuReservation Reservation;
    internal ulong Generation, View, Preparation;

    internal CoverageBatchActor(Scene3D scene, int actor)
    {
        this.scene = scene;
        Key = MotionKey.From((ulong)actor + 100);
        world = Matrix4x4.CreateTranslation((actor % 8) * 3, (actor / 8) * 3, 0);
        data = CoverageFixture.CurvedPanel(seams: actor % 2 == 0, level: actor % 2 + 1, secondRegion: actor % 3 == 0);
        var source = data.Source;
        data = data with { Source = new("batch-host-" + actor, CoverageFixture.Hash, source.Positions.ToArray(), source.Vertices, source.Triangles, source.SkinVertices.ToArray()) };
        var skeleton = new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1]);
        hosts = new SkinnedMeshHandle[data.Source.Materials.Count];
        for (int index = 0; index < hosts.Length; index++)
        {
            int slice = data.Source.Materials[index].Slice;
            var vertices = data.Source.Vertices.Select((v, i) => (v, i)).Where(p => p.v.MaterialSlice == slice)
                .OrderBy(p => p.v.SliceVertex).Select(p => data.Source.SkinVertices[p.i]).ToArray();
            uint[] indices = data.Source.Triangles.Where(t => t.MaterialSlice == slice).SelectMany(t =>
                new[] { (uint)data.Source.Vertices[t.A].SliceVertex, (uint)data.Source.Vertices[t.B].SliceVertex, (uint)data.Source.Vertices[t.C].SliceVertex }).ToArray();
            var mesh = new SkinnedGltfMesh(vertices, indices, palette, palette, skeleton);
            hosts[index] = scene.LoadSkinnedMesh(new(vertices, indices, palette, palette, skeleton)
            { Coverage = HostCoveragePart.Create(data.Map, data.Source, slice, mesh) });
        }
        conformer = scene.CreateHostConformer(skeleton, hosts);
        support = CoverageCommands.Plate(data);
        conformer.ReserveSupports([support]);
        var equipment = scene.LoadMesh(new(data.RigidPositions.Select(p => new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices));
        commands = CoverageCommands.Poses(data, Topology, support, world);
        inputs = new RigidInstanceDraw[commands.Length][];
        frozen = new RigidInstanceDraw[commands.Length][];
        for (int c = 0; c < commands.Length; c++)
        {
            commands[c] = commands[c] with { EquipmentInstanceId = (ulong)(1000 + actor * 4 + c), Id = (uint)(20 + c) };
            inputs[c] = [new(equipment, commands[c].EquipmentWorld) { ShadowOnly = true }];
            frozen[c] = new RigidInstanceDraw[1];
        }
        if (actor % 2 != 0) Array.Reverse(commands);
        SetPose(0, false);
    }

    internal void SetPose(int pose, bool refusal)
    {
        palette[0] = Matrix4x4.CreateTranslation(0, 0, pose * .005f);
        palette[1] = Matrix4x4.CreateRotationZ(pose * .005f) * Matrix4x4.CreateTranslation(0, 0, pose * .01f);
        for (int c = 0; c < commands.Length; c++)
            commands[c] = commands[c] with { MaxWorldRecession = refusal ? .01f : .8f, TransitionWeight = pose % 2 == 0 ? 1 : .8f };
    }

    internal void BuildReference()
    {
        var posed = new ModelVertex[data.Source.Positions.Length];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(data.Source, 2), data.Source, palette, posed, out var skinFailure), skinFailure.ToString());
        var final = new ModelVertex[Topology.VertexCount];
        bool accepted = CoverageReferenceConstructor.TryConstruct(Topology, posed, world, commands, new(Topology), final, out ExpectedFailure);
        ExpectedWords = MemoryMarshal.Cast<ModelVertex, uint>((accepted ? final : posed).AsSpan()).ToArray();
    }

    internal void Draw()
    {
        Staged = true;
        for (int c = 0; c < commands.Length; c++)
        {
            var command = commands[c];
            // Match the draw to the command even when submission order is reversed.
            int input = (int)command.Id - 20;
            Staged &= scene.TryStageCoverageEquipment(Key, command.Id, command.EquipmentInstanceId, support,
                inputs[input], frozen[c], out StageFailure);
            scene.Draw(frozen[c][0]);
        }
        Staged &= conformer.TryPrepare(palette, world, commands, Key, CoverageFallback.UncoveredCurrentPose, out Frame, out StageFailure);
        for (int slice = hosts.Length - 1; slice >= 0; slice--)
            scene.DrawSkinned(new(hosts[slice], world) { Coverage = Frame, Motion = Key, Tint = Color.White }, palette);
    }

    internal void CaptureIdentity()
    {
        var p = Frame.Preparation!;
        Reservation = p.GpuReservation; Generation = p.Generation; View = p.View; Preparation = p.Id;
    }

    public void Dispose() => conformer.Dispose();
}
