using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowCascadeOriginTests(ITestOutputHelper output)
{
    static readonly Vector3 LargeOrigin = new(4194304f, 128f, -4194304f);
    static readonly Vector3 Light = new(-0.55f, -0.8f, -0.25f);

    // Captured from c8f8b3495 before changing production code. Signed zeros are part of the contract.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Origin_zero_preserves_base_bits(bool orthographic)
    {
        using var rig = Create(orthographic, Vector3.Zero);
        rig.Frame();
        AssertBase(rig.Scene, orthographic);
        rig.Scene.Post.LightDirection = Light + new Vector3(0.000001f, 0f, 0f);
        rig.Frame();
        AssertBase(rig.Scene, orthographic);
    }

    static void AssertBase(Scene3D scene, bool orthographic)
    {
        int[] matrices = orthographic ? OrthographicBase : PerspectiveBase;
        Assert.Equal(matrices, MemoryMarshal.Cast<Matrix4x4, int>(scene.CascadeFitRelativeForTests).ToArray());
        Assert.Equal(matrices, MemoryMarshal.Cast<Matrix4x4, int>(scene.CascadeFitAbsoluteForTests).ToArray());
        Span<float> splits = stackalloc float[4];
        var shadows = scene.Post.Quality.Shadows;
        ShadowMapMath.FillCascadeSplits(splits, 4, shadows.ShadowNearDistance, shadows.ResolvedMaxDistance);
        Assert.Equal(new[] { 1084227584, 1099013354, 1107398785, 1114636288 },
            MemoryMarshal.Cast<float, int>(splits).ToArray());
        Vector3 held = Held(scene);
        Assert.Equal(new[] { -1089703162, -1085519305, -1098949436 },
            new[] { BitConverter.SingleToInt32Bits(held.X), BitConverter.SingleToInt32Bits(held.Y), BitConverter.SingleToInt32Bits(held.Z) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_origin_matches_origin_zero(bool orthographic)
    {
        using var zero = Create(orthographic, Vector3.Zero);
        using var far = Create(orthographic, LargeOrigin);
        zero.Frame();
        far.Frame();
        zero.Scene.Post.LightDirection = far.Scene.Post.LightDirection = Light + new Vector3(0.000001f, 0f, 0f);
        zero.Frame();
        far.Frame();
        var expected = zero.Scene.CascadeFitRelativeForTests;
        var actual = far.Scene.CascadeFitRelativeForTests;
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(actual.Length > 0);
        for (int i = 0; i < actual.Length; i++)
        {
            // Absolute error <= 1e-6 for unit-sized entries, relative error <= 1e-6 for larger entries.
            TemporalAssert.Close(expected[i], actual[i], 1e-6f, $"cascade {i}");
        }
        output.WriteLine($"orthographic={orthographic}: {actual.Length} finite cascades within 1e-6");
        Assert.Equal(Held(zero.Scene), Held(far.Scene));
    }

    [Theory]
    [InlineData(false, -2.5f, 1)]
    [InlineData(true, -2.5f, 1)]
    [InlineData(false, -2.75f, 0)]
    [InlineData(true, -2.75f, 0)]
    public void Submitted_caster_reaches_only_its_rendered_cascades(bool translated, float y, int expectedCount)
    {
        Vector3 origin = translated ? LargeOrigin : Vector3.Zero;
        using var rig = Create(orthographic: false, origin);
        // A centred box with a 0.01 m bounding-sphere radius, matching the reviewer's near-edge caster.
        GltfMesh geometry = MeshPrimitives.Box(0.02f / MathF.Sqrt(3f));
        Assert.Equal(0.01f, MeshBounds.FromVertices(geometry.Vertices).Radius, tolerance: 1e-8f);
        MeshHandle mesh = rig.Scene.LoadMesh(geometry);
        Vector3 center = origin + new Vector3(1.5f, y, 5f);
        rig.Frame(scene => scene.Draw(mesh, Matrix4x4.CreateTranslation(center)));

        Assert.Equal(1, rig.Scene.ShadowCasterCandidateCount);
        Assert.Equal(expectedCount, rig.Scene.ShadowCascadeCasterCount(0));

        // Also pin the exact review sphere and the skinned/foliage visibility predicates against this frame's fit.
        Span<byte> masks = stackalloc byte[1];
        Scene3D.ComputeCascadeMasks([new Vector4(center, 0.01f)],
            [ShadowCascadeCull.FromLightViewProj(rig.Scene.CascadeFitRelativeForTests[0], 2048)], 1, masks, origin);
        Assert.Equal((byte)expectedCount, masks[0]);
        var frustums = (FrustumPlanes[])typeof(Scene3D)
            .GetField("_shadowFrustums", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Scene)!;
        var visibility = Scene3D.ClassifySkinnedVisibility(MeshBounds.FromVertices(geometry.Vertices),
            Matrix4x4.CreateTranslation(center), true, FrustumPlanes.Extract(Matrix4x4.Identity),
            true, frustums.AsSpan(0, 1), origin);
        Assert.Equal(expectedCount == 1, visibility.VisibleShadow);
        Assert.Equal(expectedCount == 1,
            FoliageShadowSelection.IntersectsCascade(new MeshBounds(center, center), frustums[0], origin));
        output.WriteLine($"origin={origin}, relative centre={center - origin}, cascade 0 casters={rig.Scene.ShadowCascadeCasterCount(0)}");
    }

    static Vector3 Held(Scene3D scene) => (Vector3)typeof(Scene3D)
        .GetField("_heldLightDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;

    static HeadlessSceneRig Create(bool orthographic, Vector3 origin)
    {
        var rig = new HeadlessSceneRig();
        rig.Scene.RenderOrigin = origin;
        rig.Scene.CameraOverride = new FixedCamera(origin, orthographic);
        var shadows = rig.Scene.Post.Quality.Shadows;
        shadows.Mode = ShadowMode.ShadowMap;
        rig.Scene.ReplaceShadowLayout(2048, 4);
        shadows.ShadowNearDistance = 5f;
        shadows.ShadowMaxDistance = 60f;
        shadows.ShadowLightHoldTexels = 0.5f;
        rig.Scene.Post.LightDirection = Light;
        return rig;
    }

    static readonly int[] PerspectiveBase =
    [
        1040620137, -1100197948, -1120766509, 0,
        0, 1044645886, -1115567604, 0,
        -1097592766, -1109994461, -1130218353, 0,
        1057734655, 1051983869, 1060519643, 1065353216,
        1027626468, -1113008786, -1132527408, 0,
        0, 1032588836, -1128696588, 0,
        -1110208081, -1122403276, -1142435280, 0,
        -1094451200, -1101856768, 1051532174, 1065353216,
        1019856934, -1120853966, -1140472960, 0,
        0, 1024651026, -1136488262, 0,
        -1117915707, -1130297859, -1150077722, 0,
        -1086914562, -1093763070, 1045469859, 1065353216,
        1012572521, -1128273303, -1148129585, 0,
        0, 1017066472, -1143812166, 0,
        -1125089703, -1137805309, -1157381676, 0,
        -1084948481, -1091371003, 1042294327, 1065353216,
    ];

    static readonly int[] OrthographicBase =
    [
        1034717615, -1105759100, -1125859007, 0,
        0, 1039877919, -1121559039, 0,
        -1103246681, -1115050051, -1135610496, 0,
        1054212094, 1049067523, 1059713581, 1065353216,
        1030830626, -1110196148, -1130370667, 0,
        0, 1034922047, -1125607021, 0,
        -1106989881, -1119846333, -1139712005, 0,
        -1097924609, -1106051066, 1052913010, 1065353216,
        1027175734, -1113404443, -1133125000, 0,
        0, 1032260621, -1129131200, 0,
        -1110703887, -1122762964, -1142978545, 0,
        -1082785794, -1089863679, 1036885359, 1065353216,
        1022016105, -1118958626, -1139041617, 0,
        0, 1026223296, -1134406309, 0,
        -1115612741, -1128574824, -1148357289, 0,
        -1079083010, -1085915134, -1115577035, 1065353216,
    ];

    // Keep the local orientation before translating, as an origin-aware camera must.
    sealed class FixedCamera(Vector3 origin, bool orthographic) : IIsoCamera3D, IRenderOriginAware
    {
        static readonly Vector3 LocalEye = new(3f, 3f, 5f), Target = new(0f, -0.2f, 0f);
        public Vector3 RenderOrigin { get; set; }
        public Vector3 Eye => LocalEye + origin;
        public Vector3 Forward => Vector3.Normalize(Target - LocalEye);
        public Matrix4x4 Projection => orthographic ? Matrix4x4.CreateOrthographic(6f, 6f, 0.05f, 80f)
            : Matrix4x4.CreatePerspectiveFieldOfView(0.7f, 1f, 0.05f, 80f);
        public Matrix4x4 View => Matrix4x4.CreateLookAt(LocalEye + (origin - RenderOrigin),
            Target + (origin - RenderOrigin), Vector3.UnitY);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-origin)
            * Matrix4x4.CreateLookAt(LocalEye, Target, Vector3.UnitY) * Projection;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel)
        {
            Vector4 p = Vector4.Transform(new Vector4(world - RenderOrigin, 1f), ViewProjection);
            pixel = new Vector2((p.X / p.W + 1f) * width * 0.5f, (1f - p.Y / p.W) * height * 0.5f);
            return p.W > 0f;
        }
    }
}
