using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The slope-aware tile ground on <see cref="SteepWallTestData"/>'s one-tile cliff, seen from the grass
/// below and west of it with the rock plateau beyond the lip. One committed grid holds the picture, and two pixel
/// readbacks hold the two claims the grid is too coarse to state on its own: the wall face is rock although its
/// foot corners voted grass, and the face carries the checker up its height rather than one row of it smeared into
/// streaks (https://github.com/APKiwiOrg/KhaozEngine/issues/1289). Skipped unless KE_GPU_TESTS=1.</summary>
public sealed class SteepWallGoldenTests
{
    const int Width = 320;
    const int Height = 240;

    // The eye stands over the grass, west of the wall and above the plateau, looking east and down at the middle
    // of the face, so the face fills the middle of the frame and the plateau top fills the frame above it. The
    // eye's z is the target's, so the image's centre column is the plane z = -32 and the face meets it along one
    // world-vertical line.
    static readonly Vector3 Eye = new(9f, 13f, -32f);
    static readonly Vector3 Target = new(SteepWallTestData.WallX + 0.5f, 4.5f, -32f);

    // Light from the west and above, so the face turned toward the camera is lit by the key rather than by the
    // ambient alone. The rest is the background pin every tile world golden uses.
    static readonly Action<Scene3D> Configure = scene =>
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.BackgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
        scene.Post.LightDirection = new Vector3(0.6f, -0.7f, -0.3f);
    };

    // One capture serves all three rows. The scene is static, so a second render would only repeat the first.
    static readonly Lazy<byte[]> Shot = new(Capture);

    static byte[] Capture()
    {
        TileWorldCatalogs catalogs = SteepWallTestData.Catalogs();
        var options = new TileWorldViewOptions
        {
            GroundMaterials = TileGroundMaterials.Build(catalogs, SteepWallTestData.Load),
        };
        TileWorldDocument doc = SteepWallTestData.World();
        return TileWorldSnapshot.CapturePerspective(
            doc,
            catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            Eye,
            Target,
            Width,
            Height,
            options: options,
            configureScene: Configure);
    }

    [GpuFact]
    public void Golden3D_TileWorld_SteepWall()
    {
        GoldenCompare.AssertOrUpdate("tileworld_steep_wall", Shot.Value, Width, Height);
    }

    /// <summary>The middle of the face is rock. Its foot corners voted grass, so without the companion half of
    /// every tap there would be grass, and the green checker's green is two thirds again its red.</summary>
    [GpuFact]
    public void The_wall_face_renders_its_steep_companion()
    {
        byte[] rgba = Shot.Value;
        Vector3 mean = Vector3.Zero;
        int count = 0;
        for (int y = Height * 42 / 100; y < Height * 58 / 100; y++)
            for (int x = Width * 40 / 100; x < Width * 60 / 100; x++)
            {
                mean += Pixel(rgba, x, y);
                count++;
            }
        mean /= count;

        Assert.True(mean.X > 0.02f, $"the face is black ({mean}), so nothing measured below means anything.");
        float greenOverRed = mean.Y / mean.X;
        Assert.True(greenOverRed < 1.1f,
            $"the wall face averages {mean}, green {greenOverRed:F2} times red. Rock is grey and the grass foot is " +
            "green at about 1.7, so grass is still showing on the face.");
    }

    /// <summary>Down the image's centre column the face is world-vertical, so the side projection crosses a
    /// checker square every half metre and the column flips between light and dark many times. A top-down tap
    /// reads the same texture row the whole way up, which is one or two flips at most: the streak.</summary>
    [GpuFact]
    public void The_wall_face_is_not_streaked()
    {
        byte[] rgba = Shot.Value;
        int top = Height * 30 / 100, bottom = Height * 65 / 100;
        var luma = new float[bottom - top];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int y = top; y < bottom; y++)
        {
            float sum = 0f;
            for (int x = Width / 2 - 1; x <= Width / 2 + 1; x++)
            {
                Vector3 p = Pixel(rgba, x, y);
                sum += 0.2126f * p.X + 0.7152f * p.Y + 0.0722f * p.Z;
            }
            float l = sum / 3f;
            luma[y - top] = l;
            lo = MathF.Min(lo, l);
            hi = MathF.Max(hi, l);
        }

        // Counted with hysteresis around the middle of the range, so a soft mip edge is one flip and not several.
        float mid = (lo + hi) / 2f, band = (hi - lo) * 0.15f;
        int flips = 0;
        int state = 0;
        foreach (float l in luma)
        {
            int now = l > mid + band ? 1 : l < mid - band ? -1 : state;
            if (state != 0 && now != state) flips++;
            state = now;
        }

        Assert.True(hi - lo > 0.05f, $"the face's centre column is flat (luma {lo:F3} to {hi:F3}), so it carries no texture at all.");
        Assert.True(flips >= 6,
            $"the face's centre column flips between checker squares {flips} time(s) over {luma.Length} rows. A face " +
            "sampled from the side crosses one every half metre, so so few flips is the top-down streak.");
    }

    static Vector3 Pixel(byte[] rgba, int x, int y)
    {
        int at = (y * Width + x) * 4;
        return new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]) / 255f;
    }
}
