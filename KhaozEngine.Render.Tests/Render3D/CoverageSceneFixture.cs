using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed class CoverageSceneFixture : IDisposable
{
    readonly CoverageCaptureDevice device = new();
    readonly UniformBufferTrackingGpuDevice tracker;
    internal RecordingGpuCommandList Record { get; private set; } = null!;
    internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)device.Factory;
    readonly IGpuTexture targetTexture;
    readonly IGpuFramebuffer target;
    readonly CoverageFixture data;
    internal SkinnedGltfMesh HostMesh { get; private set; } = null!;
    internal GltfMesh RigidMesh { get; private set; } = null!;
    internal SkinnedGltfMesh UncoveredMesh { get; }
    internal Action<RecordingGpuCommandList.IndexedDraw>? BeforeDraw { get; set; }
    readonly MeshHandle equipment;
    readonly SkinnedMeshHandle uncovered;
    readonly RigidInstanceDraw[] stagedEquipment = new RigidInstanceDraw[1];
    readonly CoverageSupportSurface support;
    readonly SkinnedHostConformer conformer;
    public Scene3D Scene { get; }
    public SkinnedMeshHandle[] Host { get; }
    public Matrix4x4[] Palette { get; } = CoverageFixture.Pose;
    public ModelVertex[] Expected { get; }
    public ModelVertex[] Posed => data.Posed;

    public CoverageSceneFixture(bool seams = false)
    {
        data = CoverageFixture.CurvedPanel(seams: seams, level: 2);
        tracker = new(device);
        targetTexture = tracker.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, targetTexture);
        Scene = new Scene3D(tracker, target.Outputs) { UseGpuSkinning = true, FrustumCulling = false };
        Scene.Post.Starfield = false;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        Scene.Camera.Frame(Vector3.Zero, new Vector3(4, 3, 4));
        Host = LoadHost(2, out var skeleton, out var first, out var covered0);
        HostMesh = covered0;
        UncoveredMesh = first;
        uncovered = Scene.LoadSkinnedMesh(first);
        conformer = Scene.CreateHostConformer(skeleton, Host);
        support = CoverageCommands.Plate(data);
        RigidMesh = new GltfMesh(data.RigidPositions.Select(p =>
            new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices);
        equipment = Scene.LoadMesh(RigidMesh);
        Expected = new ModelVertex[conformer.Topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(conformer.Topology, data.Posed, Matrix4x4.Identity,
            Commands(), new CoverageScratch(conformer.Topology), Expected, out var failure), failure.ToString());
    }

    internal SkinnedMeshHandle[] LoadHost(int bones, out Skeleton skeleton) => LoadHost(bones, out skeleton, out _, out _);

    SkinnedMeshHandle[] LoadHost(int bones, out Skeleton skeleton, out SkinnedGltfMesh first, out SkinnedGltfMesh covered0)
    {
        int[] joints = Enumerable.Range(0, bones).ToArray();
        skeleton = new Skeleton(joints.Select(j => j - 1).ToArray(), Enumerable.Repeat(JointPose.Identity, bones).ToArray(),
            joints, joints);
        var handles = new SkinnedMeshHandle[data.Source.Materials.Count];
        first = null!; covered0 = null!;
        for (int slice = 0; slice < handles.Length; slice++)
        {
            var vertices = data.Source.Vertices.Select((v, i) => (v, i)).Where(pair => pair.v.MaterialSlice == slice)
                .OrderBy(pair => pair.v.SliceVertex).Select(pair => data.Source.SkinVertices[pair.i]).ToArray();
            var indices = data.Source.Triangles.Where(t => t.MaterialSlice == slice).SelectMany(t =>
                new[] { (uint)data.Source.Vertices[t.A].SliceVertex, (uint)data.Source.Vertices[t.B].SliceVertex,
                    (uint)data.Source.Vertices[t.C].SliceVertex }).ToArray();
            var identity = Enumerable.Repeat(Matrix4x4.Identity, bones).ToArray();
            var mesh = new SkinnedGltfMesh(vertices, indices, identity, (Matrix4x4[])identity.Clone(), skeleton);
            var covered = new SkinnedGltfMesh(vertices, indices, mesh.InverseBind, mesh.RestPose, skeleton)
            { Coverage = HostCoveragePart.Create(data.Map, data.Source, slice, mesh) };
            handles[slice] = Scene.LoadSkinnedMesh(covered);
            if (slice == 0) { first = mesh; covered0 = covered; }
        }
        return handles;
    }

    CoverageCommandPose[] Commands() => CoverageCommands.Poses(data, conformer.Topology, support, Matrix4x4.Identity);
    public void Begin() => Scene.Begin();
    public SkinnedCoverageFrame Stage()
    {
        Assert.True(Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, support,
            [new RigidInstanceDraw(equipment, Matrix4x4.Identity)], stagedEquipment, out var failure), failure.ToString());
        Assert.True(conformer.TryPrepare(Palette, Matrix4x4.Identity, Commands(), MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        Assert.Equal(CoveragePresentation.Pending, Scene.GetCoverageOutcome(frame).Presentation);
        return frame;
    }
    public SkinnedInstanceDraw HostDraw(SkinnedCoverageFrame frame, bool shadowOnly = false, int slice = 0) =>
        new(Host[slice], Matrix4x4.Identity) { Coverage = frame, Motion = MotionKey.From(10), ShadowOnly = shadowOnly };
    public SkinnedInstanceDraw UncoveredDraw() => new(uncovered, Matrix4x4.CreateTranslation(2, 0, 0));
    public void SubmitEquipment() => Scene.Draw(stagedEquipment[0]);
    public void Outline(SkinnedInstanceDraw draw) =>
        Scene.DrawSkinnedOutline(Scene.BeginMeshOutline(Color.White, 1.25f), draw, Palette);
    public void Render()
    {
        Scene.PrepareFrame();
        Record?.Dispose();
        Record = new RecordingGpuCommandList(new NullGpuCommandList())
        { CapturePayloads = true, CaptureBindings = true, UniformWindowsOfSet = tracker.WindowsOf, BeforeIndexedDraw = BeforeDraw };
        Scene.RenderInternal(Record, 32, 24, target);
    }
    internal void RenderUncaptured(IGpuCommandList cl)
    { Scene.PrepareFrame(); Scene.RenderInternal(cl, 32, 24, target); }
    public void AssertCanonicalDraws(SkinnedCoverageFrame frame)
    {
        for (int slice = 0; slice < Host.Length; slice++)
        {
            var range = conformer.Topology.MaterialRanges[slice];
            var route = Scene.CoverageSliceForTests(frame, slice);
            var draws = Record.IndexedDraws.Where(d => ReferenceEquals(d.IndexBuffer, route.Indices)).ToArray();
            Assert.NotEmpty(draws);
            Assert.All(draws, d =>
            {
                Assert.Same(route.Vertices, d.VertexBuffer);
                Assert.Equal(route.BaseVertex, d.VertexOffset);
                Assert.Equal((uint)route.IndexCount, d.IndexCount);
            });
            var upload = Assert.Single(Record.Uploads, u => ReferenceEquals(u.Buffer, route.Vertices));
            Assert.Equal(Expected.AsSpan(range.FirstVertex, range.VertexCount).ToArray(),
                MemoryMarshal.Cast<byte, ModelVertex>(upload.Data).Slice(route.BaseVertex, range.VertexCount).ToArray());
            uint[] expectedIndices = conformer.Topology.Indices.Slice(range.FirstIndex, range.IndexCount).ToArray();
            for (int i = 0; i < expectedIndices.Length; i++) expectedIndices[i] -= (uint)range.FirstVertex;
            Assert.Equal(expectedIndices, MemoryMarshal.Cast<byte, uint>(device.Uploads[route.Indices]).ToArray());
            int firstDraw = Record.IndexedDraws.ToList().FindIndex(d => ReferenceEquals(d.IndexBuffer, route.Indices));
            Assert.True(upload.DrawsBefore <= firstDraw);
        }
    }
    internal bool IsHostIndexBuffer(IGpuBuffer? buffer) => conformer.Indices.Contains(buffer);
    internal void AssertCapturedMatricesUploaded(SkinnedCoverageFrame frame)
    {
        foreach (var pass in Scene.CoveragePassesForTests(frame))
        {
            Matrix4x4 matrix = pass.ViewProjection;
            byte[] bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in matrix, 1)).ToArray();
            Assert.Contains(Record.Uploads, upload => upload.Data is not null && upload.Data.AsSpan().IndexOf(bytes) >= 0);
        }
    }

    public void AssertFallbackDraws(SkinnedCoverageFrame frame)
    {
        var route = Scene.CoverageFallbackSliceForTests(frame, 0);
        var draws = Record.IndexedDraws.Where(d => ReferenceEquals(d.IndexBuffer, route.Indices)).ToArray();
        Assert.NotEmpty(draws);
        Assert.All(draws, d =>
        {
            Assert.Same(route.Vertices, d.VertexBuffer);
            Assert.Equal(0, d.VertexOffset);
            Assert.Equal((uint)HostMesh.Indices32.Length, d.IndexCount);
        });
        Assert.Equal(0, Scene.CoveredHostPreparationCount);
    }
    internal void AssertLegacyOutline((IGpuBuffer Vertices, IGpuBuffer Indices) route)
    {
        Assert.Contains(Record.IndexedDraws, d => d.Pipeline is FakePipeline { Request: { } request }
            && request.FragmentGlsl == KhaozEngine.Render3D.Internal.ShaderSources.TargetOutlineFullMaskFrag
            && ReferenceEquals(d.VertexBuffer, route.Vertices) && ReferenceEquals(d.IndexBuffer, route.Indices));
        Assert.DoesNotContain(Record.IndexedDraws, d => IsHostIndexBuffer(d.IndexBuffer));
    }
    public void EnablePointShadows()
    {
        Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
        Scene.Begin();
        AddPoint();
        Render();
    }
    public void AddPoint() => Scene.AddLight(new(0, 0, 4), Color.White, 25, 1, LightShadow.Dynamic);
    internal MeshHandle RigidHandle => equipment;
    internal CoverageSupportSurface Support => support;
    internal RigidInstanceDraw EquipmentDraw => stagedEquipment[0];
    internal SkinnedHostConformer Conformer => conformer;
    internal CoverageCommandPose[] CommandPoses => Commands();
    public void Dispose()
    {
        Record?.Dispose();
        conformer.Dispose();
        Scene.Dispose();
        target.Dispose();
        targetTexture.Dispose();
        device.Dispose();
    }
}
