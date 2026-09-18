using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The point-light shadow PASS on a real device, read back texel by texel. This is the test that keeps the two
/// halves of the feature honest: the pass reaches an atlas texel through a baked matrix and the receiver reaches
/// it through <see cref="PointShadowMath.FaceAndUv"/>'s table, and nothing but a readback can say the two agree.
/// A face landing mirrored or one column over is a wall shadowed on the wrong side, which renders as plausible
/// lighting rather than as a failure, so it has to be measured rather than reasoned about.
/// <para>
/// Each case puts one unit cube three metres down one axis from a light of radius 10 and asserts three things
/// about the atlas: that cube's own cell centre holds 0.25 (the 2.5 m near face over the 10 m radius), that cell's
/// corners still hold the cleared 1.0 (the cube subtends about 22 degrees of a 90 degree face, so it cannot reach
/// them), and the other five faces plus the untouched row are 1.0 throughout.
/// </para>
/// <para>
/// Fifteen captures through one fixture scene, so the class follows the <c>OceanFocusScene</c> rule and shares one
/// <see cref="Scene3D"/> rather than paying pipeline creation nine times.
/// </para>
/// </summary>
public sealed class PointShadowPassGpuTests(PointShadowPassScene fixture) : IClassFixture<PointShadowPassScene>
{
    /// <summary>The light radius every case uses, which is also the face far plane and the divisor the stored
    /// value is normalized by.</summary>
    const float Radius = 10f;

    /// <summary>How far down its axis the cube's centre stands, so its near face is at 2.5 and the cell centre
    /// must read 0.25.</summary>
    const float CubeDistance = 3f;

    const float Expected = (CubeDistance - 0.5f) / Radius;

    static readonly Vector3[] Axes =
    {
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
    };

    // The two unit vectors perpendicular to a face's own axis, so a cube can be put off-centre in both of that
    // face's in-face coordinates at once.
    static (Vector3 A, Vector3 B) Perpendiculars(int face) => face switch
    {
        0 or 1 => (Vector3.UnitY, Vector3.UnitZ),
        2 or 3 => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ACubeDownOneAxisLandsInThatAxisFaceAtTheDistanceItStandsAt(int face)
    {
        Vector3 light = PointShadowPassScene.LightPos;
        Vector3 dir = Axes[face];
        PointShadowPassScene.Atlas atlas = fixture.RenderCube(light + dir * CubeDistance, light, Radius);

        PointShadowMath.FaceAndUv(dir, out int table, out Vector2 uv);
        Assert.Equal(face, table);

        float centre = atlas.At(PointShadowMath.AtlasUv(face, PointShadowPassScene.Slot, atlas.Rows, uv));
        Assert.True(MathF.Abs(centre - Expected) <= 0.05f,
            $"face {face}'s cell centre holds {centre}, expected about {Expected}");

        // The cube reaches about 11 degrees off the axis, so the cell's own corners are clear. That is a
        // containment check and NOT a mirror check: a cube on the axis lands at (0.5, 0.5), which every mirror
        // of this face maps to itself. The off-axis case below is what sees a mirror.
        foreach ((float u, float v) in new[] { (0.02f, 0.02f), (0.98f, 0.02f), (0.02f, 0.98f), (0.98f, 0.98f) })
            Assert.Equal(1f, atlas.At(
                PointShadowMath.AtlasUv(face, PointShadowPassScene.Slot, atlas.Rows, new Vector2(u, v))), 3);

        // Every other face of this light, and the whole of the row nobody rendered.
        for (int other = 0; other < PointShadowMath.FaceCount; other++)
        {
            if (other == face) continue;
            Assert.Equal(1f, atlas.At(PointShadowMath.AtlasUv(
                other, PointShadowPassScene.Slot, atlas.Rows, new Vector2(0.5f, 0.5f))), 3);
        }
        Assert.Equal(1f, atlas.MinInRow(0), 3);
    }

    /// <summary>
    /// THE MIRROR CASE, which the on-axis cases above cannot be: a cube OFF the axis, one metre along one in-face
    /// perpendicular and half a metre along the other, so its uv is away from the face centre in both coordinates
    /// and every mirror of the face moves it. The atlas must hold the near distance at the uv
    /// <see cref="PointShadowMath.FaceAndUv"/> names and the untouched clear at the mirrored uv, which is what
    /// says the baked matrix and the receiver's table agree on the SIGN of each in-face axis rather than only on
    /// the column.
    /// </summary>
    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnOffAxisCubeLandsAtTheUvTheTableNamesAndNotAtItsMirror(int face)
    {
        Vector3 light = PointShadowPassScene.LightPos;
        (Vector3 a, Vector3 b) = Perpendiculars(face);
        Vector3 dir = Axes[face] * CubeDistance + a * 1f + b * 0.5f;
        PointShadowPassScene.Atlas atlas = fixture.RenderCube(light + dir, light, Radius);

        PointShadowMath.FaceAndUv(dir, out int table, out Vector2 uv);
        Assert.Equal(face, table);
        Assert.True(MathF.Abs(uv.X - 0.5f) > 0.05f && MathF.Abs(uv.Y - 0.5f) > 0.05f,
            $"face {face}'s off-axis cube lands at {uv}, which is too near the centre to see a mirror");

        // The ray through the cube's centre enters through the cube's near face on the major axis, so the stored
        // distance is that entry point's, over the radius.
        float major = MathF.Max(MathF.Abs(dir.X), MathF.Max(MathF.Abs(dir.Y), MathF.Abs(dir.Z)));
        float expected = dir.Length() * (major - 0.5f) / major / Radius;
        float hit = atlas.At(PointShadowMath.AtlasUv(face, PointShadowPassScene.Slot, atlas.Rows, uv));
        Assert.True(MathF.Abs(hit - expected) <= 0.05f,
            $"face {face} holds {hit} at the uv the table names, expected about {expected}");

        Vector2 mirrored = new(1f - uv.X, 1f - uv.Y);
        float atMirror = atlas.At(PointShadowMath.AtlasUv(
            face, PointShadowPassScene.Slot, atlas.Rows, mirrored));
        Assert.True(MathF.Abs(atMirror - 1f) <= 1e-3f,
            $"face {face} holds {atMirror} at the MIRRORED uv {mirrored}, where nothing was drawn. The face is "
            + "mirrored between the baked matrix and the table the receiver samples with.");
    }

    [GpuFact]
    public void ACubeOutsideTheLightRadiusIsCulledAndLeavesTheRowCleared()
    {
        Vector3 light = PointShadowPassScene.LightPos;
        PointShadowPassScene.Atlas atlas = fixture.RenderCube(light + new Vector3(40f, 0f, 0f), light, Radius);
        for (int face = 0; face < PointShadowMath.FaceCount; face++)
            Assert.Equal(1f, atlas.At(PointShadowMath.AtlasUv(
                face, PointShadowPassScene.Slot, atlas.Rows, new Vector2(0.5f, 0.5f))), 3);
        Assert.Equal(0, atlas.Draws);
    }

    [GpuFact]
    public void RenderingARowTwiceClearsWhatTheLastPassWroteIntoIt()
    {
        Vector3 light = PointShadowPassScene.LightPos;
        fixture.RenderCube(light + Vector3.UnitX * CubeDistance, light, Radius);
        // The same light with the cube moved onto another axis: the +X cell must go back to cleared rather than
        // keep the last pass's distance, which is the whole job of the per-row clear quad.
        PointShadowPassScene.Atlas atlas = fixture.RenderCube(light + Vector3.UnitZ * CubeDistance, light, Radius);

        Assert.Equal(1f, atlas.At(PointShadowMath.AtlasUv(
            0, PointShadowPassScene.Slot, atlas.Rows, new Vector2(0.5f, 0.5f))), 3);
        float lit = atlas.At(PointShadowMath.AtlasUv(
            4, PointShadowPassScene.Slot, atlas.Rows, new Vector2(0.5f, 0.5f)));
        Assert.True(MathF.Abs(lit - Expected) <= 0.05f, $"the +Z cell holds {lit}, expected {Expected}");
    }
}

/// <summary>
/// The one scene <see cref="PointShadowPassGpuTests"/> renders every configuration through, held for the whole
/// class as an xUnit class fixture (the <c>OceanFocusScene</c> rule: a class capturing more than a couple of
/// pictures shares one <see cref="Scene3D"/>). The device is created LAZILY, so a plain <c>dotnet test</c> that
/// skips every <c>[GpuFact]</c> never asks for one.
/// </summary>
/// <remarks>
/// Each render is a two-step drive, which is the same shape the cascade readback tests use. First an ORDINARY
/// FRAME through <see cref="Render3DPreview.Capture"/>, because that is what groups the queued instances and
/// uploads the shared instance buffer the point pass then reuses without a second upload. Then
/// <c>Scene3D.DebugRenderPointShadowSlot</c>, which records the pass on a command list of its own and fences,
/// exactly as <c>DebugReadShadowMap</c> does for its copy. The integration half will call the same pass from
/// inside the frame instead.
/// </remarks>
public sealed class PointShadowPassScene : IDisposable
{
    /// <summary>Preview size. Small on purpose: nothing here reads the picture, only the atlas.</summary>
    public const int Width = 96, Height = 64;

    /// <summary>Cell size per axis. Small enough to read back cheaply on lavapipe and WARP, big enough that the
    /// cube covers many texels at the centre and none at the corners.</summary>
    public const int FaceResolution = 64;

    /// <summary>Atlas rows. Two, so there is always a row nobody rendered to check against.</summary>
    public const int Rows = 2;

    /// <summary>The row every case renders into. Not row 0, so a bake that ignored the slot would be caught.</summary>
    public const int Slot = 1;

    /// <summary>Where the light stands. Deliberately off the origin, so a matrix that dropped the light
    /// translation would land the cube in the wrong cell.</summary>
    public static Vector3 LightPos => new(4f, 2f, -3f);

    GpuDeviceContext? _gpu;
    Render3DPreview? _preview;
    MeshHandle _cube;

    /// <summary>One atlas readback plus the caster draw count the pass reported.</summary>
    public readonly record struct Atlas(float[] Texels, int Width, int Height, int Rows, int Draws)
    {
        /// <summary>The texel at an atlas UV, nearest, clamped inside the atlas.</summary>
        public float At(Vector2 uv)
        {
            int x = Math.Clamp((int)(uv.X * Width), 0, Width - 1);
            int y = Math.Clamp((int)(uv.Y * Height), 0, Height - 1);
            return Texels[y * Width + x];
        }

        /// <summary>The smallest texel anywhere in one light row, so a whole untouched row is pinned as cleared
        /// in one assertion rather than by sampling six cell centres and hoping nothing landed between them.
        /// </summary>
        public float MinInRow(int slot)
        {
            int rowHeight = Height / Math.Max(1, Rows);
            float min = float.PositiveInfinity;
            for (int y = slot * rowHeight; y < (slot + 1) * rowHeight; y++)
                for (int x = 0; x < Width; x++)
                    min = MathF.Min(min, Texels[y * Width + x]);
            return min;
        }
    }

    /// <summary>Queue one unit cube at <paramref name="cubeCentre"/>, render an ordinary frame so the instance
    /// buffer is uploaded, then render the point-shadow row for a light at <paramref name="lightPos"/> and read
    /// the atlas back.</summary>
    public Atlas RenderCube(Vector3 cubeCentre, Vector3 lightPos, float radius)
    {
        Scene3D scene = Scene;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;   // the key light is not what this class measures
        scene.Camera.Frame(lightPos, lightPos + new Vector3(8f, 6f, 8f));
        _preview!.Capture(s => s.Draw(_cube, Matrix4x4.CreateTranslation(cubeCentre), Color.White));
        Assert.True(scene.EnsurePointShadowAtlas(FaceResolution, Rows));
        int draws = scene.DebugRenderPointShadowSlot(Slot, lightPos, radius);
        float[] texels = scene.DebugReadPointShadowAtlas(out int w, out int h);
        return new Atlas(texels, w, h, Rows, draws);
    }

    Scene3D Scene
    {
        get
        {
            if (_preview != null) return _preview.Scene;
            _gpu = GpuDeviceContext.CreateHeadless();
            _preview = new Render3DPreview(_gpu.GpuDevice, Width, Height);
            _preview.Scene.Post.Starfield = false;
            _preview.Scene.Post.TransparentBackground = false;
            _preview.Scene.EffectTimeSeconds = 0f;
            _cube = _preview.Scene.LoadMesh(MeshPrimitives.Box(1f));
            return _preview.Scene;
        }
    }

    public void Dispose()
    {
        _gpu?.GpuDevice.WaitForIdle();
        _preview?.Dispose();
        _gpu?.Dispose();
        _preview = null;
        _gpu = null;
    }
}
