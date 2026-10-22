using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqMaterialGpuTests(DqMaterialScene fixture, ITestOutputHelper output) : IClassFixture<DqMaterialScene>
{
    [GpuFact]
    public void DQ_material_attributes_match_the_CSharp_reference()
    {
        IGpuDevice device = fixture.Device;
        int draws = 0;
        foreach (float scale in new[] { 1f, 1048576f })
        {
            Matrix4x4[] matrices = DqGpuReadback.Twist.Concat(new[]
            {
                Matrix4x4.CreateRotationX(.73f) * Matrix4x4.CreateTranslation(3 * scale, -2 * scale, scale),
                Matrix4x4.CreateRotationY(-1.1f) * Matrix4x4.CreateTranslation(-scale, scale, -2 * scale),
            }).ToArray();
            var palette = matrices.Select((m, i) => DualQuaternionPalette.FromComposed(m, new("native synthetic", null, 0), i)).ToArray();
            foreach (Vector4 weights in new[] { new Vector4(.5f, .5f, 0, 0), new Vector4(.1f, .2f, .3f, .4f),
                Vector4.UnitX, Vector4.UnitZ, Vector4.Zero })
            foreach (bool tangent in new[] { false, true })
            {
                var source = new SkinnedVertex
                {
                    Position = new Vector3(1, -.25f, .5f) * scale,
                    Normal = Vector3.Normalize(new Vector3(.2f, -.4f, .9f)), Color = Vector4.One,
                    Uv = new Vector2(.2f, .7f), BoneIndices = new Vector4(0, 1, 2, 3), BoneWeights = weights,
                    Tangent = tangent ? new Vector4(.8f, .3f, -.1f, -1) : Vector4.Zero,
                };
                ModelVertex native = DqGpuReadback.Capture(device, source, palette, matrices);
                ModelVertex expected = SkinningEvaluation.Vertex(source, SkinningProfile.DualQuaternionRigid, [], palette);
                float L = MathF.Max(1, MathF.Max(source.Position.Length(), matrices.Max(m => m.Translation.Length())));
                for (int axis = 0; axis < 3; axis++)
                    Assert.InRange(MathF.Abs(native.Position[axis] - expected.Position[axis]), 0f, 2e-5f * L);
                AssertDirection(native.Normal, expected.Normal);
                if (tangent)
                {
                    AssertDirection(new Vector3(native.Tangent.X, native.Tangent.Y, native.Tangent.Z),
                        new Vector3(expected.Tangent.X, expected.Tangent.Y, expected.Tangent.Z));
                    Assert.Equal(BitConverter.SingleToInt32Bits(source.Tangent.W), BitConverter.SingleToInt32Bits(native.Tangent.W));
                }
                else AssertWords(Vector4.Zero, native.Tangent);
                if (weights == Vector4.Zero || weights == Vector4.UnitX) AssertWords(source.Position, native.Position);
                if (weights.X == .5f)
                    Assert.True(Vector3.Distance(native.Position, SkinningMath.SkinVertex(source, matrices).Position) > .2f * scale,
                        "The half-twist witness must separate DQ from LBS.");
                draws++;
            }
        }
        // Exact identity bypass preserves source signed zeros, and absent tangents ignore even a nonzero W.
        var identitySource = new SkinnedVertex
        {
            Position = new Vector3(-0f, 2, -0f), Normal = Vector3.UnitY, Color = Vector4.One,
            BoneWeights = Vector4.UnitX, Tangent = new Vector4(0, 0, 0, -1),
        };
        ModelVertex identity = DqGpuReadback.Capture(device, identitySource,
            [new RigidDualQuaternion(new Vector4(0, 0, 0, 1), Vector4.Zero)], [Matrix4x4.Identity]);
        AssertWords(identitySource.Position, identity.Position);
        AssertWords(Vector4.Zero, identity.Tangent);
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: attribute witness draws={draws + 1}, " +
            "position=2e-5*L, direction vector=2e-5, angle=1e-4 radians, identity words exact");
    }

    [GpuFact]
    public void DQ_materials_preserve_maps_cutout_splits_and_both_dissolve_senses()
    {
        DqMaterialScene scene = fixture;
        byte[] solid = scene.Capture(gpu: true);
        Assert.True(Covered(solid) > 100, "The DQ material witness must draw pixels.");
        AssertImageNear(solid, scene.Capture(gpu: false));
        byte[] flat = scene.Capture(gpu: true, normalMap: false);
        Assert.True(Different(solid, flat) > 50, "The normal map must alter the DQ material's lighting.");
        byte[] cutout = scene.Capture(gpu: true, cutout: true);
        Assert.InRange(Covered(cutout), 20, Covered(solid) - 20);
        byte[] ordinary = scene.Capture(gpu: true, dissolve: .5f);
        byte[] complement = scene.Capture(gpu: true, dissolve: .5f, complement: true);
        Assert.InRange(Covered(ordinary), 10, Covered(solid) - 10);
        Assert.InRange(Covered(complement), 10, Covered(solid) - 10);
        AssertImageNear(ordinary, scene.Capture(gpu: false, dissolve: .5f));
        AssertImageNear(complement, scene.Capture(gpu: false, dissolve: .5f, complement: true));
        for (int i = 0; i < solid.Length; i += 4)
            if (solid[i + 3] > 200)
                Assert.True((ordinary[i + 3] > 200) != (complement[i + 3] > 200), "Dissolve keep sets must complement.");
        byte[] mixed = scene.Capture(gpu: true, mixed: true);
        AssertImageNear(mixed, scene.Capture(gpu: false, mixed: true));
        Assert.True(Different(solid, mixed) > 50, "The neighbouring LBS actor must keep its distinct deformation.");
        output.WriteLine($"{scene.Device.Backend} {scene.Device.Capabilities.DeviceName}: " +
            "nonzero split-material DQ draws, normal map, cutout, both dissolve senses, mixed LBS/DQ, CPU/GPU pixel parity");
    }

    [GpuFact]
    public void Shared_scene_matches_fresh_after_method_material_and_execution_changes()
    {
        fixture.Capture(gpu: false, cutout: true);
        fixture.Capture(gpu: true, dissolve: .7f, complement: true);
        fixture.Capture(gpu: false, mixed: true, normalMap: false);
        byte[] aged = fixture.Capture(gpu: true, mixed: true);
        using var fresh = new DqMaterialScene(fixture.Device);
        byte[] baseline = fresh.Capture(gpu: true, mixed: true);
        Assert.True(Covered(baseline) > 100);
        Assert.Equal(baseline, aged);
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: shared/fresh RGBA words identical");
    }

    static void AssertDirection(Vector3 actual, Vector3 expected)
    {
        Assert.InRange(Vector3.Distance(actual, expected), 0f, 2e-5f);
        // Double-precision atan2 avoids the binary32 acos floor above the required 1e-4 radian limit.
        double cx = (double)actual.Y * expected.Z - (double)actual.Z * expected.Y;
        double cy = (double)actual.Z * expected.X - (double)actual.X * expected.Z;
        double cz = (double)actual.X * expected.Y - (double)actual.Y * expected.X;
        double dot = (double)actual.X * expected.X + (double)actual.Y * expected.Y + (double)actual.Z * expected.Z;
        Assert.InRange(Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot), 0, 1e-4);
    }

    static void AssertWords(Vector3 expected, Vector3 actual)
    {
        for (int i = 0; i < 3; i++) Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(actual[i]));
    }

    static void AssertWords(Vector4 expected, Vector4 actual)
    {
        for (int i = 0; i < 4; i++) Assert.Equal(BitConverter.SingleToInt32Bits(expected[i]), BitConverter.SingleToInt32Bits(actual[i]));
    }

    static int Covered(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 200);
    static int Different(byte[] a, byte[] b) => Enumerable.Range(0, a.Length / 4)
        .Count(i => Math.Abs(a[i * 4] - b[i * 4]) + Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) + Math.Abs(a[i * 4 + 2] - b[i * 4 + 2]) > 6);

    static void AssertImageNear(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int outside = Enumerable.Range(0, actual.Length).Count(i => Math.Abs(actual[i] - expected[i]) > 3);
        Assert.True(outside < actual.Length / 200, $"CPU/GPU material mismatch at {outside}/{actual.Length} channels.");
    }
}

public sealed class DqMaterialScene : IDisposable
{
    const int Size = 128;
    readonly IGpuDevice? _sharedDevice;
    GpuDeviceContext? _context;
    Render3DPreview? _preview;
    readonly SkinnedGltfMesh[] _meshes = new SkinnedGltfMesh[3];
    readonly SkinnedMeshHandle[,] _handles = new SkinnedMeshHandle[3, 3];

    public DqMaterialScene() { }
    internal DqMaterialScene(IGpuDevice device) => _sharedDevice = device;
    internal IGpuDevice Device => _sharedDevice ?? (_context ??= GpuDeviceContext.CreateHeadless()).GpuDevice;

    void EnsureScene()
    {
        if (_preview is not null) return;
        _preview = new Render3DPreview(Device, Size, Size);
        Scene3D scene = _preview.Scene;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.AmbientColor = new Color(.15f, .15f, .15f, 1);
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        // Compare per-fragment keep sets at native size. The default 1600x900 target blurs alpha on a 128px blit.
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Camera.Frame(Vector3.Zero, new Vector3(0, 0, 4));
        _meshes[0] = DqGpuReadback.Patch(SkinningProfile.DualQuaternionRigid);
        _meshes[1] = DqGpuReadback.Patch(SkinningProfile.DualQuaternionRigid, right: true);
        _meshes[2] = DqGpuReadback.Patch(SkinningProfile.LinearBlend, right: true);
        var normal = scene.LoadTexture([215, 128, 215, 255], 1, 1);
        var left = scene.LoadTexture([230, 100, 60, 255], 1, 1);
        var right = scene.LoadTexture([60, 130, 230, 255], 1, 1);
        var mask = scene.LoadTexture([255, 255, 255, 0, 255, 255, 255, 255], 2, 1);
        for (int i = 0; i < _meshes.Length; i++)
        {
            var albedo = i == 0 ? left : right;
            _handles[i, 0] = scene.LoadSkinnedMesh(_meshes[i], new Scene3D.SurfaceMaps(albedo, normal));
            _handles[i, 1] = scene.LoadSkinnedMesh(_meshes[i], new Scene3D.SurfaceMaps(albedo));
            _handles[i, 2] = scene.LoadSkinnedMesh(_meshes[i], new Scene3D.SurfaceMaps(mask, normal, alphaCutoff: .5f));
        }
    }

    internal byte[] Capture(bool gpu, bool normalMap = true, bool cutout = false, float dissolve = 0,
        bool complement = false, bool mixed = false)
    {
        EnsureScene();
        _preview!.Scene.UseGpuSkinning = gpu;
        _preview.Capture(scene =>
        {
            foreach (int i in new[] { 0, mixed ? 2 : 1 })
            {
                var draw = new SkinnedInstanceDraw(_handles[i, cutout ? 2 : normalMap ? 0 : 1],
                    Matrix4x4.CreateScale(1.1f, .9f, 1) * Matrix4x4.CreateTranslation(.1f, -.1f, 0))
                {
                    CastsShadows = false, Tint = Color.White, Material = Material.None,
                    Dissolve = dissolve, DissolveComplement = complement ? 1 : 0,
                    DissolveEdgeColor = new Color(0, 1, 0, 1), DissolveEdgeWidth = .1f,
                };
                DqGpuReadback.Queue(scene, _meshes[i], draw, DqGpuReadback.Twist);
            }
        });
        return _preview.ReadbackRgba();
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _context?.Dispose();
    }
}
