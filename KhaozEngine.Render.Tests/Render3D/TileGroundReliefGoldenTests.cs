using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The relief tile-ground pipeline on a real device, with absolute pixel assertions rather than a committed grid, so
/// each test states the behaviour it proves: the normal map's tangent frame, the height blend's winner, a map-less
/// material drawing as the plain pipeline draws it, the feather breakup, and hex-tiling. Named Golden so the
/// cross-platform matrix runs them on every backend. Each capture builds and drops its own device, so the class
/// needs no serial collection.
/// <para>The view is orthographic and nearly straight down (azimuth 0, so screen +x is world +x, and the top of
/// the image is world -z), 32 pixels to the metre. Specular is off unless a test is about it, and the fill light
/// is black, so brightness reads as the key light's N.L.</para>
/// </summary>
public sealed class TileGroundReliefGoldenTests(ITestOutputHelper output)
{
    const int Size = 160;
    const float PixelsPerMetre = 32f;   // Size / OrthoSize

    // ---- The tangent frame -------------------------------------------------------------------------------------

    [GpuFact]
    public void A_normal_map_tilts_the_lit_ground_along_its_projection_axes_Golden()
    {
        // Four slots, each a uniform normal map tilted one way in tangent space: +x, -x, +y (toward the image top)
        // and -y, laid out as four strips west to east. Under a light from the east the +x strip is lit and the -x
        // strip is not. Under a light from the north (world -z, the image top of a top-down tap) +y is lit.
        Vector3[] tilts = { new(0.6f, 0f, 0.8f), new(-0.6f, 0f, 0.8f), new(0f, 0.6f, 0.8f), new(0f, -0.6f, 0.8f) };
        var layers = new TileGroundLayerImage[4];
        for (int i = 0; i < 4; i++)
            layers[i] = new TileGroundLayerImage
            {
                AlbedoRgba = Solid(4, 180, 180, 180, 255),
                SurfaceRgba = Solid(4, Encode(tilts[i].X), Encode(tilts[i].Y), Encode(tilts[i].Z), 128),
                NormalMapped = true,
            };
        GltfMesh strips = Strips(4);

        byte[] east = Capture(scene => scene.LoadMesh(strips, scene.LoadTileGroundMaterial(4, 4, layers, 4, 4, 0f)),
            light: new Vector3(-1f, -0.6f, 0f));
        byte[] north = Capture(scene => scene.LoadMesh(strips, scene.LoadTileGroundMaterial(4, 4, layers, 4, 4, 0f)),
            light: new Vector3(0f, -0.6f, 1f));

        float[] e = new float[4], n = new float[4];
        for (int i = 0; i < 4; i++)
        {
            e[i] = Luma(east, StripCentre(i), Size / 2);
            n[i] = Luma(north, StripCentre(i), Size / 2);
        }
        output.WriteLine($"east light: +x {e[0]:F0} -x {e[1]:F0} +y {e[2]:F0} -y {e[3]:F0}");
        output.WriteLine($"north light: +x {n[0]:F0} -x {n[1]:F0} +y {n[2]:F0} -y {n[3]:F0}");

        Assert.True(e[0] > e[1] + 40, "tangent +x is not world +x on the top-down projection");
        Assert.True(Math.Abs(e[2] - e[3]) < 12, "a light along x lit the two y tilts differently");
        Assert.True(n[2] > n[3] + 40, "tangent +y (the image top) is not world -z on the top-down projection");
        Assert.True(Math.Abs(n[0] - n[1]) < 12, "a light along z lit the two x tilts differently");
    }

    // ---- The height blend --------------------------------------------------------------------------------------

    [GpuFact]
    public void The_higher_surface_wins_where_materials_meet_Golden()
    {
        // Red carries a height map, high on its west half and low on its east half. Blue has none, so it sits at the
        // neutral height. Both weigh 0.5 everywhere, which a linear blend draws purple: resolved by height, red
        // holds the west half and blue fills the east.
        var red = new TileGroundLayerImage
        {
            AlbedoRgba = HalfHeight(8, 220, 30, 30), TilesPerMetre = 0.25f, HeightMapped = true,
        };
        var blue = new TileGroundLayerImage { AlbedoRgba = Solid(8, 30, 30, 220, 255), TilesPerMetre = 0.25f };
        byte[] rgba = Capture(scene => scene.LoadMesh(Quad(0f, 4f, Even(), Even(), 0f, 1f, 0f),
            scene.LoadTileGroundMaterial(8, 8, new[] { red, blue }, 0, 0, 0f)), target: new Vector3(2f, 0f, 0f));

        (int wr, int wg, int wb) = Patch(rgba, Size / 2 - (int)PixelsPerMetre, Size / 2);
        (int er, int eg, int eb) = Patch(rgba, Size / 2 + (int)PixelsPerMetre, Size / 2);
        output.WriteLine($"west=({wr},{wg},{wb}) east=({er},{eg},{eb})");
        Assert.True(wr > wb + 60, $"the high red half did not win: ({wr},{wg},{wb})");
        Assert.True(eb > er + 60, $"the map-less blue did not fill the low half: ({er},{eg},{eb})");
    }

    [GpuFact]
    public void A_mapless_material_in_a_relief_set_draws_as_the_plain_set_draws_it_Golden()
    {
        // A red to blue corner ramp, lit with specular on. The relief set adds an unused third layer that carries a
        // breakup, which selects the relief program and changes nothing the two drawn layers use.
        TileGroundLayerImage Red() => new() { AlbedoRgba = Solid(4, 220, 40, 40, 255) };
        TileGroundLayerImage Blue() => new() { AlbedoRgba = Solid(4, 40, 40, 220, 255) };
        GltfMesh ramp = Quad(-2f, 2f, new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), 0f, 1f, 0f);

        byte[] plain = Capture(scene => scene.LoadMesh(ramp,
            scene.LoadTileGroundMaterial(4, 4, new[] { Red(), Blue() }, 0, 0, 0.35f)), light: new Vector3(-0.4f, -0.8f, -0.45f));
        byte[] relief = Capture(scene => scene.LoadMesh(ramp,
            scene.LoadTileGroundMaterial(4, 4, new[] { Red(), Blue(), new TileGroundLayerImage
            {
                AlbedoRgba = Solid(4, 0, 255, 0, 255), FeatherBreakup = 0.5f,
            } }, 0, 0, 0.35f)), light: new Vector3(-0.4f, -0.8f, -0.45f));

        int worst = 0, lit = 0;
        for (int i = 0; i < plain.Length; i += 4)
        {
            if (plain[i] + plain[i + 1] + plain[i + 2] > 60) lit++;
            for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(plain[i + c] - relief[i + c]));
        }
        output.WriteLine($"lit pixels {lit}, worst channel difference {worst}");
        Assert.True(lit > Size * Size / 2, "the ramp did not render");
        Assert.True(worst <= 2, $"a map-less material drew differently in the relief program: {worst}");
    }

    // ---- The feather breakup -----------------------------------------------------------------------------------

    [GpuFact]
    public void Breakup_wanders_a_feathered_edge_inward_and_keeps_its_ends_Golden()
    {
        // A feathered overlay ramp, red underlay at the west edge (overlay weight 0) to the blue overlay at the east
        // edge (weight 1). Down the column where the overlay weight is 0.5 a straight feather is one colour, and a
        // broken-up one varies with the world-space noise. Near the west edge the broken edge never carries more
        // overlay than the straight one, so the fringe at the tile boundary stays soft, and the interior stays blue.
        (float plainSpread, int plainWestBlue, _) = Feather(0f);
        (float brokenSpread, int brokenWestBlue, int eastRed) = Feather(1f);
        output.WriteLine($"blue spread down the mid column: straight {plainSpread:F1}, broken {brokenSpread:F1}; " +
                         $"west edge blue straight {plainWestBlue}, broken {brokenWestBlue}; broken east edge red {eastRed}");

        Assert.True(plainSpread < 3f, $"a straight feather varies along its edge: {plainSpread}");
        Assert.True(brokenSpread > 12f, $"the breakup did not move the edge: {brokenSpread}");
        Assert.True(brokenWestBlue <= plainWestBlue + 3,
            $"the breakup reached past the straight feather at the boundary: {brokenWestBlue} > {plainWestBlue}");
        Assert.True(eastRed < 50, $"the breakup let the underlay into the overlay's interior: {eastRed}");
    }

    (float Spread, int WestBlue, int EastRed) Feather(float breakup)
    {
        var red = new TileGroundLayerImage { AlbedoRgba = Solid(4, 220, 30, 30, 255) };
        var blue = new TileGroundLayerImage { AlbedoRgba = Solid(4, 30, 30, 220, 255), FeatherBreakup = breakup };
        // Overlay slot 1 in Tangent.w (slot plus one): a ramp over x in [0, 2], underlay weights falling from 1 to 0,
        // then the overlay's interior over [2, 4], where its weight is exactly 1.
        GltfMesh ramp = Quad(0f, 2f, new Vector4(1f, 0f, 0f, 0f), Vector4.Zero, 0f, 0f, 2f);
        GltfMesh interior = Quad(2f, 4f, Vector4.Zero, Vector4.Zero, 0f, 0f, 2f);
        var mesh = new GltfMesh([.. ramp.Vertices, .. interior.Vertices], new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });
        byte[] rgba = Capture(scene => scene.LoadMesh(mesh,
            scene.LoadTileGroundMaterial(4, 4, new[] { red, blue }, 0, 0, 0f)), target: new Vector3(2f, 0f, 0f));

        int mid = Size / 2 - (int)PixelsPerMetre, west = Size / 2 - (int)(1.85f * PixelsPerMetre);
        int east = Size / 2 + (int)PixelsPerMetre;
        double sum = 0, sumSq = 0; int n = 0;
        for (int y = Size / 2 - 56; y <= Size / 2 + 56; y += 4)
        {
            int b = rgba[(y * Size + mid) * 4 + 2];
            sum += b; sumSq += (double)b * b; n++;
        }
        double mean = sum / n;
        return ((float)Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)), Patch(rgba, west, Size / 2).B,
            Patch(rgba, east, Size / 2).R);
    }

    // ---- Hex-tiling --------------------------------------------------------------------------------------------

    [GpuTheory]
    [InlineData(TileGroundAntiTiling.Offset)]
    [InlineData(TileGroundAntiTiling.Rotate)]
    public void Hex_tiling_breaks_a_visible_repeat_Golden(TileGroundAntiTiling mode)
    {
        // A texture that differs everywhere inside one repeat, tiled once per metre. Plain, the image matches itself
        // one metre east. Hex-tiled, the copies one metre apart come from differently placed hexes.
        float plain = RepeatDifference(TileGroundAntiTiling.None);
        float hex = RepeatDifference(mode);
        output.WriteLine($"mean difference one repeat apart: plain {plain:F1}, {mode} {hex:F1}");
        Assert.True(plain < 4f, $"the plain texture is not periodic at its repeat: {plain}");
        Assert.True(hex > plain + 15f, $"{mode} hex-tiling left the repeat visible: {hex}");
    }

    float RepeatDifference(TileGroundAntiTiling mode)
    {
        var layer = new TileGroundLayerImage { AlbedoRgba = Gradient(16), TilesPerMetre = 1f, AntiTiling = mode };
        // The relief program, also for the plain capture, through a second unused layer carrying a breakup.
        var selector = new TileGroundLayerImage { AlbedoRgba = Solid(16, 0, 0, 0, 255), FeatherBreakup = 0.5f };
        byte[] rgba = Capture(scene => scene.LoadMesh(Quad(-2f, 2f, Even1(), Even1(), 0f, 0f, 0f),
            scene.LoadTileGroundMaterial(16, 16, new[] { layer, selector }, 0, 0, 0f)));
        int period = (int)PixelsPerMetre;
        long total = 0; int n = 0;
        for (int y = Size / 2 - 50; y <= Size / 2 + 50; y += 2)
            for (int x = Size / 2 - 60; x + period <= Size / 2 + 60; x += 2)
                for (int c = 0; c < 3; c++)
                {
                    total += Math.Abs(rgba[(y * Size + x) * 4 + c] - rgba[(y * Size + x + period) * 4 + c]);
                    n++;
                }
        return (float)total / n;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    byte[] Capture(Func<Scene3D, MeshHandle> setup, Vector3? light = null, Vector3? target = null)
    {
        MeshHandle mesh = default;
        return Render3DSnapshot.Capture(Size, Size, setup: scene =>
        {
            mesh = setup(scene);
            scene.Post.Outline = false;
            scene.Post.LightDirection = light ?? new Vector3(-0.3f, -1f, -0.2f);
            scene.Post.LightColor = new Color(1f, 1f, 1f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = new Color(0.08f, 0.08f, 0.08f, 1f);
            scene.Camera.Azimuth = 0f;
            scene.Camera.Elevation = 1.5f;
            scene.Camera.Target = target ?? Vector3.Zero;
            scene.Camera.AspectRatio = 1f;
            scene.Camera.Zoom = 1f;
            scene.Camera.OrthoSize = Size / PixelsPerMetre;
        }, drawFrame: scene => scene.Draw(mesh, Matrix4x4.Identity));
    }

    // Four 1 m strips west to east across x in [-2, 2], z in [-2, 2], strip i drawn entirely with slot i.
    static GltfMesh Strips(int count)
    {
        var verts = new ModelVertex[count * 4];
        var indices = new ushort[count * 6];
        var normal = Vector3.UnitY;
        var weight = new Vector4(1f, 0f, 0f, 0f);
        for (int i = 0; i < count; i++)
        {
            float x0 = -2f + i, x1 = x0 + 1f;
            var uv = new Vector2(i, i);
            var tangent = new Vector4(i, i, 1f, 0f);
            verts[i * 4] = new ModelVertex(new Vector3(x0, 0f, -2f), normal, weight, uv, tangent);
            verts[i * 4 + 1] = new ModelVertex(new Vector3(x1, 0f, -2f), normal, weight, uv, tangent);
            verts[i * 4 + 2] = new ModelVertex(new Vector3(x1, 0f, 2f), normal, weight, uv, tangent);
            verts[i * 4 + 3] = new ModelVertex(new Vector3(x0, 0f, 2f), normal, weight, uv, tangent);
            ushort b = (ushort)(i * 4);
            indices[i * 6] = b; indices[i * 6 + 1] = (ushort)(b + 1); indices[i * 6 + 2] = (ushort)(b + 2);
            indices[i * 6 + 3] = b; indices[i * 6 + 4] = (ushort)(b + 2); indices[i * 6 + 5] = (ushort)(b + 3);
        }
        return new GltfMesh(verts, indices);
    }

    static int StripCentre(int i) => (int)(Size / 2 + (-1.5f + i) * PixelsPerMetre);

    // A quad over x in [x0, x1], z in [-2, 2], with the given west and east weights over slots (slotA, slotB,
    // slotA, slotB) and Tangent.w as given (an overlay slot plus one, or 0).
    static GltfMesh Quad(float x0, float x1, Vector4 west, Vector4 east, float slotA, float slotB, float overlay)
    {
        var normal = Vector3.UnitY;
        var uv = new Vector2(slotA, slotB);
        var tangent = new Vector4(slotA, slotB, 1f, overlay);
        var verts = new[]
        {
            new ModelVertex(new Vector3(x0, 0f, -2f), normal, west, uv, tangent),
            new ModelVertex(new Vector3(x1, 0f, -2f), normal, east, uv, tangent),
            new ModelVertex(new Vector3(x1, 0f, 2f), normal, east, uv, tangent),
            new ModelVertex(new Vector3(x0, 0f, 2f), normal, west, uv, tangent),
        };
        return new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 });
    }

    static Vector4 Even() => new(0.5f, 0.5f, 0f, 0f);
    static Vector4 Even1() => new(1f, 0f, 0f, 0f);

    static byte Encode(float n) => (byte)Math.Clamp(MathF.Round((n * 0.5f + 0.5f) * 255f), 0f, 255f);

    static byte[] Solid(int size, byte r, byte g, byte b, byte a)
    {
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
        return px;
    }

    // A solid colour whose alpha, the height, is 1 on the west half of the image (u below 0.5) and 0 on the east.
    static byte[] HalfHeight(int size, byte r, byte g, byte b)
    {
        byte[] px = Solid(size, r, g, b, 0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size / 2; x++) px[(y * size + x) * 4 + 3] = 255;
        return px;
    }

    // Red rises with u, green with v and blue alternates per texel, so no two texels of one repeat match.
    static byte[] Gradient(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                px[i] = (byte)(x * 255 / (size - 1));
                px[i + 1] = (byte)(y * 255 / (size - 1));
                px[i + 2] = (byte)(((x + y) & 1) * 200);
                px[i + 3] = 255;
            }
        return px;
    }

    static (int R, int G, int B) Patch(byte[] rgba, int cx, int cy)
    {
        long r = 0, g = 0, b = 0; int n = 0;
        for (int y = cy - 3; y <= cy + 3; y++)
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                int i = (y * Size + x) * 4;
                r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
            }
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    static float Luma(byte[] rgba, int cx, int cy)
    {
        (int r, int g, int b) = Patch(rgba, cx, cy);
        return 0.299f * r + 0.587f * g + 0.114f * b;
    }
}
