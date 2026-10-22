using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqConsumerMatrixGpuTests(DqConsumerMatrixScene fixture, ITestOutputHelper output)
    : IClassFixture<DqConsumerMatrixScene>
{
    [GpuFact]
    public void Every_consumer_uses_the_same_selected_DQ_surface()
    {
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}");
        // This first capture bypasses only the temporary public admission gate, using the established
        // stage-3 harness. At red, real rest-bound culling drops this posed surface before any pass.
        var retained = fixture.Capture(true, false, 0, internalAdmission: true);
        Assert.True(retained.Drawn == 1, "The posed DQ surface outside the rest heuristic must reach the native colour pass.");
        bool mainObserved = false, cascadeObserved = false, pointObserved = false, outlineObserved = false;
        int cases = 0;
        foreach (bool gpu in new[] { false, true })
        foreach (bool shifted in new[] { false, true })
        foreach (int submission in new[] { 0, 1, 2 }) // colour, shadow-only, outline-only
        {
            var actual = fixture.Capture(gpu, shifted, submission);
            var reference = fixture.Capture(false, shifted, submission, reference: true);
            Assert.Equal(submission == 0 ? 1 : 0, actual.Drawn);
            if (submission == 0)
            {
                Assert.True(actual.Colour.Count(p => p > 0) > 0);
                Assert.InRange(actual.Colour.Zip(reference.Colour).Count(p => Math.Abs(p.First - p.Second) > 3), 0, 16);
                mainObserved = true;
            }
            if (submission != 2)
            {
                AssertDepth(reference.Cascades, actual.Cascades, "cascade");
                AssertDepth(reference.Points, actual.Points, "point");
                cascadeObserved = pointObserved = true;
            }
            if (submission != 1)
            {
                Assert.Contains(actual.Outline, p => p != 0);
                Assert.InRange(actual.Outline.Zip(reference.Outline).Count(p => (p.First > 0) != (p.Second > 0)), 0, 8);
                outlineObserved = true;
            }
            output.WriteLine($"gpu={gpu} shifted={shifted} submission={submission} colour={actual.Drawn} " +
                $"cascade={actual.Cascades.Count(Written)} point={actual.Points.Count(Written)} outline={actual.Outline.Count(p => p > 0)}");
            cases++;
        }
        SkinnedGltfMesh mesh = DqCullingTests.Patch();
        Matrix4x4[] joints = DqCullingTests.Joints();
        var store = new PreparedSkinningStore();
        PreparedSkinningPose pose = store.Prepare(mesh, joints, new("consumer matrix readback", null, 0));
        Matrix4x4 world = Matrix4x4.CreateTranslation(-39.1f, 0, 0);
        Assert.True(DualQuaternionBounds.TryBoundWorld(pose.ModelBounds, world, out MeshBounds worldBounds));
        foreach (SkinnedVertex vertex in mesh.Vertices)
        {
            Vector3 nativePosition = Vector3.Transform(DqGpuReadback.Capture(fixture.Device, vertex,
                store.Dq(pose).ToArray(), joints).Position, world);
            Vector3 independent = new(.9f - vertex.Position.Y, vertex.Position.X, 0);
            Assert.InRange(Vector3.Distance(independent, nativePosition), 0, 2e-5f * 40);
            for (int axis = 0; axis < 3; axis++)
                Assert.InRange(nativePosition[axis], worldBounds.Min[axis], worldBounds.Max[axis]);
        }
        Assert.True(mainObserved && cascadeObserved && pointObserved && outlineObserved);
        Assert.Equal(12, cases);
        output.WriteLine("12/12 public consumer cases and four native position readbacks inside the finite posed envelope");
    }

    [GpuFact]
    public void Reused_scene_matches_fresh_with_frozen_pose_inputs()
    {
        fixture.Capture(false, true, 1, internalAdmission: true);
        fixture.Capture(true, false, 2, internalAdmission: true);
        var aged = fixture.Capture(true, true, 0, internalAdmission: true);
        using var fresh = new DqConsumerMatrixScene(fixture.Device);
        var baseline = fresh.Capture(true, true, 0, internalAdmission: true);
        Assert.True(aged.Drawn == 1, "A reused scene must retain the posed surface beyond its rest bound.");
        Assert.Equal(baseline.Colour, aged.Colour);
        Assert.Equal(baseline.Cascades, aged.Cascades);
        Assert.Equal(baseline.Points, aged.Points);
        Assert.Equal(baseline.Outline, aged.Outline);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh frozen-input surfaces identical");
    }

    static bool Written(float d) => float.IsFinite(d) && d < 1;
    static void AssertDepth(float[] expected, float[] actual, string pass)
    {
        Assert.Contains(actual, Written);
        int different = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(float.IsFinite(actual[i]));
            if (Written(expected[i]) != Written(actual[i])) different++;
            else Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, 2e-5f);
        }
        Assert.InRange(different, 0, 8);
    }
}

public sealed class DqConsumerMatrixScene : IDisposable
{
    const int Size = 64;
    readonly IGpuDevice? _shared;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    readonly SkinnedGltfMesh _mesh = DqCullingTests.Patch();
    SkinnedGltfMesh? _reference;
    SkinnedMeshHandle _handle, _referenceHandle;
    public DqConsumerMatrixScene() { }
    internal DqConsumerMatrixScene(IGpuDevice device) => _shared = device;
    internal IGpuDevice Device => _shared ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;
    internal sealed record Surfaces(byte[] Colour, float[] Cascades, float[] Points, byte[] Outline, int Drawn);

    internal Surfaces Capture(bool gpu, bool shifted, int submission, bool reference = false, bool internalAdmission = false)
    {
        if (_preview is null)
        {
            _preview = new Render3DPreview(Device, Size, Size,
                new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 256, ShadowCascadeCount = 2 });
            Scene3D initial = _preview.Scene;
            DqCascadeScene.Configure(initial);
            initial.Post.Pixelated = false;
            initial.Camera.OrthoSize = 2;
            initial.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
            initial.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 2;
            initial.Post.Quality.Shadows.PointShadows.MaxDynamicLightsPerFrame = 1;
            _handle = initial.LoadSkinnedMesh(_mesh);
            SkinnedVertex[] vertices = (SkinnedVertex[])_mesh.Vertices.Clone();
            for (int i = 0; i < vertices.Length; i++)
                vertices[i].Position = new Vector3(-vertices[i].Position.Y, vertices[i].Position.X, 0);
            _reference = new SkinnedGltfMesh(vertices, _mesh.Indices32, _mesh.InverseBind, _mesh.RestPose, null);
            _referenceHandle = initial.LoadSkinnedMesh(_reference);
            // Resolve static residency and allocate the current-pose transient row before readback.
            _preview.Capture(Lights);
            _preview.Capture(Lights);
        }
        Scene3D scene = _preview.Scene;
        scene.UseGpuSkinning = gpu;
        scene.RenderOrigin = shifted ? new Vector3(32, -16, 8) : Vector3.Zero;
        scene.Camera.Target = shifted ? new Vector3(.1f, -.1f, 0) : Vector3.Zero;
        scene.Camera.Azimuth = shifted ? .15f : 0;
        _preview.Capture(s =>
        {
            Lights(s);
            SkinnedGltfMesh mesh = reference ? _reference! : _mesh;
            SkinnedMeshHandle handle = reference ? _referenceHandle : _handle;
            Matrix4x4[] joints = reference ? mesh.RestPose : DqCullingTests.Joints();
            Matrix4x4 world = Matrix4x4.CreateTranslation(reference ? .9f : -39.1f, 0, 0);
            if (submission != 2)
            {
                var draw = new SkinnedInstanceDraw(handle, world)
                { ShadowOnly = submission == 1, Material = Material.Glowing(Color.White) };
                if (internalAdmission) DqGpuReadback.Queue(s, mesh, draw, joints);
                else s.DrawSkinned(draw, joints);
            }
            if (submission != 1)
            {
                MeshOutlineGroup group = s.BeginMeshOutline(Color.White, 1, MeshOutlineOcclusion.None);
                if (internalAdmission) DqOutlineScene.Queue(s, mesh, group, handle, joints, world);
                else s.DrawSkinnedOutline(group, handle, joints, world);
            }
        });
        // A static point row becomes resident on its following frame. Dynamic and transient current-pose
        // surfaces are read independently, so none of these comparisons uses cached skinned geometry.
        float[] point = submission == 2 ? [] : ReadDepth(scene.PointShadowTexture!)
            .Concat(scene.PointShadowTransientTexture is { } transient ? ReadDepth(transient) : []).ToArray();
        var renderer = (ModelRenderer)DqGpuReadback.Field(scene, "_model");
        byte[] outline = submission == 1 ? [] : ReadBytes((IGpuTexture)DqGpuReadback.Field(
            DqGpuReadback.Field(scene, "_targetOutlines"), "_fullCoverage"));
        return new(GpuReadback.ToRgba(Device, (IGpuTexture)DqGpuReadback.Field(_preview, "_target"), Size, Size),
            submission == 2 ? [] : ReadDepth(renderer.ShadowMap.ShadowTexture), point, outline, scene.DrawnSkinnedInstances);
    }

    static void Lights(Scene3D scene)
    {
        scene.AddLight(new Vector3(-.5f, 0, 1), Color.White, 4, 1, LightShadow.Static(777));
        scene.AddLight(new Vector3(-.5f, 0, 1), Color.White, 4, 1, LightShadow.Dynamic);
    }

    float[] ReadDepth(IGpuTexture texture) => MemoryMarshal.Cast<byte, float>(Read(texture, 4)).ToArray();
    byte[] ReadBytes(IGpuTexture texture) => Read(texture, 1);
    byte[] Read(IGpuTexture texture, int pixelBytes)
    {
        using var staging = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            texture.Width, texture.Height, texture.Format, GpuTextureUsage.Staging));
        using var commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, "DQ consumer surface readback")) commands.CopyTexture(texture, staging);
        Device.Submit(commands);
        Device.WaitForIdle();
        int rowBytes = checked((int)texture.Width * pixelBytes);
        byte[] bytes = new byte[checked(rowBytes * (int)texture.Height)];
        MappedData map = Device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(IntPtr.Add(map.Data, checked(y * (int)map.RowPitch)), bytes, y * rowBytes, rowBytes);
        }
        finally { Device.Unmap(staging); }
        return bytes;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
