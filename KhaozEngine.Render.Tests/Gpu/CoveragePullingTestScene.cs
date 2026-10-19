using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoveragePullingTestScene : IDisposable
{
    readonly IGpuDevice device;
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    internal Scene3D Scene { get; }
    internal Actor[] Actors { get; private set; }
    readonly Actor[][] variants;

    internal CoveragePullingTestScene(IGpuDevice device)
    {
        this.device = device;
        texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(160, 120,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, texture);
        Scene = new(device, target.Outputs) { FrustumCulling = false };
        Scene.Post.Starfield = false;
        Scene.Post.Sky.Enabled = Scene.Post.Atmosphere.Enabled = false;
        Scene.Post.Outline = Scene.Post.Hdr.Enabled = Scene.Post.Bloom.Enabled = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Scene.Post.BackgroundColor = new(.02f, .15f, .035f, 1);
        Scene.Post.AmbientColor = new(.5f, .5f, .5f, 1);
        Scene.Camera.Target = new(1.5f, 0, 0);
        Scene.Camera.Distance = 6;
        Scene.Camera.OrthoSize = 4;
        Scene.Camera.Azimuth = Scene.Camera.Elevation = 0;
        variants = [
            [new(Scene, 0, false, false), new(Scene, 1, false, false)],
            [new(Scene, 0, true, false), new(Scene, 1, true, false)],
            [new(Scene, 0, false, true), new(Scene, 1, false, true)],
        ];
        Actors = variants[0];
    }

    internal void Select(bool refusal, bool materialEffects) => Actors = variants[refusal ? 1 : materialEffects ? 2 : 0];

    internal (byte[] Colour, CoverageTargetReadback Full, CoverageTargetReadback Visible) Capture(
        bool coverage, bool gpuSkinning, bool outlineOnly)
    {
        Scene.UseGpuSkinning = gpuSkinning;
        Scene.Begin();
        Scene.EffectTimeSeconds = 0;
        foreach (var actor in Actors) actor.Draw(coverage, outlineOnly);
        Scene.PrepareFrame(160, 120);
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "CoveragePulling")) Scene.RenderInternal(commands, 160, 120, target);
        device.Submit(commands); device.WaitForIdle(); device.Present();
        var outlines = Scene.ReadCoverageOutlinesForTests();
        return (GpuReadback.ToRgba(device, texture, 160, 120), outlines.Full, outlines.Visible);
    }

    internal sealed class Actor : IDisposable
    {
        readonly Scene3D scene;
        readonly CoverageFixture data;
        readonly SkinnedHostConformer conformer;
        readonly CoverageSupportSurface support;
        readonly SkinnedMeshHandle[] hosts, references;
        readonly MeshHandle equipment;
        readonly Matrix4x4 world;
        readonly bool materialEffects, complement;
        readonly Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        internal MotionKey Key { get; }
        internal SkinnedCoverageFrame Frame;
        internal ModelVertex[] Posed { get; }
        internal ModelVertex[] Final { get; }

        internal Actor(Scene3D scene, int actor, bool refusal, bool materialEffects)
        {
            this.scene = scene;
            this.materialEffects = materialEffects; complement = actor != 0;
            Key = MotionKey.From((ulong)actor + 10);
            world = Matrix4x4.CreateTranslation(actor * 3, 0, 0);
            data = CoverageFixture.CurvedPanel(seams: true, level: 2);
            var authored = data.Source.SkinVertices.ToArray();
            for (int i = 0; i < authored.Length; i++)
                authored[i].Uv = new((authored[i].Position.X + 1.2f) / 2.4f, (authored[i].Position.Y + 1.2f) / 2.4f);
            data = data with
            {
                Source = new(data.Source.Identity, CoverageFixture.Hash,
                data.Source.Positions.ToArray(), data.Source.Vertices, data.Source.Triangles, authored)
            };
            if (refusal)
            {
                var topology = CoverageRefinement.Build(data.Source, data.Map);
                var v = topology.Vertices.First(v => v.SourceVertex < 0 && (v.A == 0 && v.B == v.C || v.B == 0 && v.A == v.C || v.C == 0 && v.A == v.B));
                var triangle = data.Source.Triangles[v.SourceTriangle];
                var skin = data.Source.SkinVertices.ToArray();
                int[] corners = [triangle.A, triangle.B, triangle.C];
                int[] weights = [v.A, v.B, v.C];
                var pair = corners.Where((_, i) => weights[i] > 0).ToArray();
                skin[pair[0]].Normal = Vector3.UnitX;
                skin[pair[1]].Normal = -Vector3.UnitX;
                data = data with
                {
                    Source = new(data.Source.Identity, CoverageFixture.Hash,
                    data.Source.Positions.ToArray(), data.Source.Vertices, data.Source.Triangles, skin)
                };
            }
            var skeleton = new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1]);
            hosts = new SkinnedMeshHandle[2]; references = new SkinnedMeshHandle[2];
            var maps = materialEffects ? new Scene3D.SurfaceMaps(scene.LoadTexture(
                [255, 80, 120, 0, 80, 160, 255, 255], 2, 1), alphaCutoff: .5f) : default;
            for (int slice = 0; slice < 2; slice++)
            {
                var vertices = data.Source.Vertices.Select((v, i) => (v, i)).Where(p => p.v.MaterialSlice == slice)
                    .OrderBy(p => p.v.SliceVertex).Select(p => data.Source.SkinVertices[p.i]).ToArray();
                var indices = data.Source.Triangles.Where(t => t.MaterialSlice == slice).SelectMany(t =>
                    new[] { (uint)data.Source.Vertices[t.A].SliceVertex, (uint)data.Source.Vertices[t.B].SliceVertex,
                        (uint)data.Source.Vertices[t.C].SliceVertex }).ToArray();
                var mesh = new SkinnedGltfMesh(vertices, indices, palette, palette, skeleton);
                hosts[slice] = scene.LoadSkinnedMesh(new(vertices, indices, palette, palette, skeleton)
                { Coverage = HostCoveragePart.Create(data.Map, data.Source, slice, mesh) }, maps);
            }
            conformer = scene.CreateHostConformer(skeleton, hosts);
            support = CoverageCommands.Plate(data);
            conformer.ReserveSupports([support]);
            equipment = scene.LoadMesh(new(data.RigidPositions.Select(p => new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices));
            Posed = new ModelVertex[data.Source.Positions.Length];
            Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(data.Source, 2), data.Source, palette, Posed, out _));
            Final = new ModelVertex[conformer.Topology.VertexCount];
            var commands = CoverageCommands.Poses(data, conformer.Topology, support, Matrix4x4.Identity);
            bool accepted = CoverageReferenceConstructor.TryConstruct(conformer.Topology, Posed, Matrix4x4.Identity,
                commands, new(conformer.Topology), Final, out var failure);
            Assert.Equal(!refusal, accepted);
            if (refusal) Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
            for (int slice = 0; slice < 2; slice++)
            {
                var range = conformer.Topology.MaterialRanges[slice];
                var vertices = refusal ? Posed.Where((_, i) => data.Source.Vertices[i].MaterialSlice == slice).ToArray()
                    : Final.AsSpan(range.FirstVertex, range.VertexCount).ToArray();
                uint[] indices = refusal ? data.Source.Triangles.Where(t => t.MaterialSlice == slice).SelectMany(t =>
                    new[] { (uint)data.Source.Vertices[t.A].SliceVertex, (uint)data.Source.Vertices[t.B].SliceVertex,
                        (uint)data.Source.Vertices[t.C].SliceVertex }).ToArray()
                    : conformer.Topology.Indices.Slice(range.FirstIndex, range.IndexCount).ToArray().Select(i => i - (uint)range.FirstVertex).ToArray();
                references[slice] = scene.LoadSkinnedMesh(new(vertices.Select(v => new SkinnedVertex
                { Position = v.Position, Normal = v.Normal, Color = v.Color, Uv = v.Uv, Tangent = v.Tangent }).ToArray(),
                    indices, palette, palette, skeleton), maps);
            }
        }

        internal void Draw(bool coverage, bool outlineOnly)
        {
            Frame = default;
            if (coverage)
            {
                var rigid = new RigidInstanceDraw(equipment, world) { ShadowOnly = true };
                var staged = new RigidInstanceDraw[1];
                Assert.True(scene.TryStageCoverageEquipment(Key, 1, 1, support, [rigid], staged, out var failure), failure.ToString());
                var commands = CoverageCommands.Poses(data, conformer.Topology, support, world);
                Assert.True(conformer.TryPrepare(palette, world, commands, Key, CoverageFallback.UncoveredCurrentPose,
                    out Frame, out failure), failure.ToString());
                scene.Draw(staged[0]);
            }
            var group = scene.BeginMeshOutline(Color.White, 1);
            for (int slice = 0; slice < 2; slice++)
            {
                var draw = new SkinnedInstanceDraw(coverage ? hosts[slice] : references[slice], world)
                {
                    Coverage = Frame,
                    Motion = Key,
                    Tint = slice == 0 ? Color.White : new Color(.4f, .7f, 1, 1),
                    Dissolve = materialEffects ? .4f : 0,
                    DissolveComplement = materialEffects && complement ? 1 : 0
                };
                if (!outlineOnly) scene.DrawSkinned(draw, palette);
                scene.DrawSkinnedOutline(group, draw, palette);
            }
        }
        public void Dispose() => conformer.Dispose();
    }

    public void Dispose()
    {
        foreach (var variant in variants) foreach (var actor in variant) actor.Dispose();
        Scene.Dispose(); target.Dispose(); texture.Dispose();
    }
}
