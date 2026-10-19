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

internal sealed partial class CoverageSceneFixture : IDisposable
{
    readonly CoverageCaptureDevice device;
    readonly UniformBufferTrackingGpuDevice tracker;
    internal RecordingGpuCommandList Record { get; private set; } = null!;
    internal FakeGpuResourceFactory Factory => device.RecordingInner!;
    internal CoverageCaptureDevice Capture => device;
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

    public CoverageSceneFixture(bool seams = false, GpuBackendKind backend = GpuBackendKind.Vulkan,
        bool supportsCompute = true, bool failCoverageBuild = false, IGpuDevice? nativeDevice = null, bool executeNative = false)
    {
        if (executeNative) nativeDevice = (nativeContext = GpuDeviceContext.CreateHeadless()).GpuDevice;
        device = new(backend, supportsCompute, failCoverageBuild, nativeDevice);
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
        conformer.ReserveSupports([support]);
        RigidMesh = new GltfMesh(data.RigidPositions.Select(p =>
            new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices);
        equipment = Scene.LoadMesh(RigidMesh);
        Expected = new ModelVertex[conformer.Topology.VertexCount];
        Assert.True(CoverageGeometryTestRun.TryEvaluate(conformer.Topology, data.Posed, Matrix4x4.Identity,
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
    public SkinnedCoverageFrame Stage(Matrix4x4? model = null, ulong instance = 1, float weight = 1, float? recession = null)
    {
        Matrix4x4 world = model ?? Matrix4x4.Identity;
        var commands = Commands();
        for (int i = 0; i < commands.Length; i++) commands[i] = commands[i] with
        {
            EquipmentInstanceId = instance,
            EquipmentWorld = world,
            CoverageWorld = support.CoverageLocal * world,
            TransitionWeight = weight,
            MaxWorldRecession = recession ?? commands[i].MaxWorldRecession
        };
        Assert.True(Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, instance, support,
            [new RigidInstanceDraw(equipment, world) { ShadowOnly = NativeEquipmentShadowOnly }], stagedEquipment, out var failure), failure.ToString());
        Assert.True(conformer.TryPrepare(Palette, world, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        Assert.Equal(CoveragePresentation.Pending, Scene.GetCoverageOutcome(frame).Presentation);
        LastCoverageFrame = frame;
        return frame;
    }
    public SkinnedInstanceDraw HostDraw(SkinnedCoverageFrame frame, bool shadowOnly = false, int slice = 0) =>
        new(Host[slice], frame.Preparation?.Model ?? Matrix4x4.Identity) { Coverage = frame, Motion = MotionKey.From(10), ShadowOnly = shadowOnly };
    public SkinnedInstanceDraw UncoveredDraw() => new(uncovered, Matrix4x4.CreateTranslation(2, 0, 0));
    public void SubmitEquipment() => Scene.Draw(stagedEquipment[0]);
    public void Outline(SkinnedInstanceDraw draw) =>
        Scene.DrawSkinnedOutline(Scene.BeginMeshOutline(Color.White, 1.25f), draw, Palette);
    public void Render(int width = 32, int height = 24, bool prepare = true)
    {
        if (nativeContext is not null) { RenderNative(width, height, prepare); return; }
        if (prepare) Scene.PrepareFrame(width, height);
        Record?.Dispose();
        Record = new RecordingGpuCommandList(new NullGpuCommandList())
        { CapturePayloads = true, CaptureBindings = true, UniformWindowsOfSet = tracker.WindowsOf, BeforeIndexedDraw = BeforeDraw };
        Record.BeforeNonIndexedDraw = (bindings, _, _, _, _) => BeforeNonIndexedDraw?.Invoke(bindings);
        nativeSubmission ??= new(device);
        nativeSubmission.Begin();
        try
        {
            Scene.RenderInternal(Record, width, height, target);
            Scene.RecordCoverageReadbacks(Record, nativeSubmission);
            nativeSubmission.Submit(Record);
        }
        catch { nativeSubmission.Abandon(); throw; }
    }
    internal void RenderUncaptured(IGpuCommandList cl)
    { Scene.PrepareFrame(32, 24); Scene.RenderInternal(cl, 32, 24, target); }
    public void AssertCanonicalDraws(SkinnedCoverageFrame frame) => AssertCoverageDispatch(frame);
    internal void AssertCoverageDispatch(SkinnedCoverageFrame frame)
    {
        var batch = Scene.CoverageGpuBatchForTests!;
        var actor = frame.Preparation!.GpuReservation;
        Assert.Contains(batch.Actors.ToArray(), a => a == actor);
        Assert.Equal(conformer.Topology.VertexCount, actor.VertexCount);
        Assert.Contains(Factory.ResourceSets, set => set.Resources.Length == 6
            && ReferenceEquals(set.Resources[0], batch.StaticData) && ReferenceEquals(set.Resources[1], batch.FrameData)
            && ReferenceEquals(set.Resources[2], batch.Work) && ReferenceEquals(set.Resources[3], batch.Current)
            && ReferenceEquals(set.Resources[4], batch.Previous) && ReferenceEquals(set.Resources[5], batch.Results));
    }
    internal bool IsHostIndexBuffer(IGpuBuffer? buffer) => conformer.Indices.Contains(buffer);
    internal void AssertCapturedMatricesUploaded(SkinnedCoverageFrame frame)
    {
        AssertCoverageDispatch(frame);
        Assert.Contains(Record.Uploads, u => ReferenceEquals(u.Buffer, Scene.CoverageGpuBatchForTests!.FrameData));
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
        nativeSubmission?.Dispose();
        Record?.Dispose();
        conformer.Dispose();
        Scene.Dispose();
        target.Dispose();
        targetTexture.Dispose();
        device.Dispose();
        nativeContext?.Dispose();
    }
}
