using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One hosted T4 capture under OC8. Frame-block costs are reported, never asserted.</summary>
[Collection("GpuTimingSensitive")]
public sealed class InteriorCutCostPerfGpuTests(ITestOutputHelper output)
{
    const int Width = 960, Height = 600, Cascades = 4, Resolution = 2048;
    const int Rounds = 15, Warm = 2, Block = 12;
    const double BudgetMs = .1;

    internal enum Mode { ShadowsOff, NoOptIn, EmptyField, VillageInteriors }

    internal static Mode[] OrderFor(int round) =>
        Enumerable.Range(0, 4).Select(i => (Mode)((round + i) % 4)).ToArray();

    internal static bool IsCaptureDevice(string backend, string deviceName)
    {
        if (backend is not ("direct3d11-native" or "vulkan-native")) return false;
        string[] words = deviceName.Split([' ', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        return words.Contains("NVIDIA", StringComparer.OrdinalIgnoreCase)
            && words.Contains("T4", StringComparer.OrdinalIgnoreCase);
    }

    [GpuFact(RequiresRealGpu = true)]
    public void VillageCutCostIsRecorded()
    {
        using var fixture = new TemporalFixture(Width, Height);
        string backend = fixture.Device.Backend switch
        {
            GpuBackendKind.Direct3D11Native => "direct3d11-native",
            GpuBackendKind.VulkanNative => "vulkan-native",
            _ => fixture.Device.Backend.ToString(),
        };
        string device = fixture.Device.Capabilities.DeviceName;
        output.WriteLine($"{backend} on {device}");
        if (!IsCaptureDevice(backend, device))
        {
            output.WriteLine("not measured: interior cut timing is reserved for hosted NVIDIA T4 adapters");
            return;
        }

        var inputs = InteriorCutCostScene.Create();
        Scene3D scene = fixture.Scene;
        scene.EnableTiming = true;
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.ShadowLightHoldTexels = 0f;
        scene.RequestShadowMapLayout(Resolution, Cascades);
        scene.Camera.Azimuth = .6f;
        scene.Camera.Elevation = .55f;
        scene.Camera.Frame(Vector3.Zero, new Vector3(250f, 20f, 250f));
        MeshHandle ground = scene.LoadMesh(MeshPrimitives.Tile(500f, .2f));
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        Scene3D.TextureHandle leaves = scene.LoadTexture(LeafPixels(), 8, 8);
        MeshHandle card = scene.LoadMesh(MeshPrimitives.Plane(10f, 14f),
            new Scene3D.SurfaceMaps(leaves, alphaCutoff: .5f));
        Mode active = Mode.ShadowsOff;
        int submittedTrees = 0;
        void Draw(Scene3D s, int frame)
        {
            float azimuth = inputs.SunAngleFor(frame) * MathF.PI / 180f, elevation = 35f * MathF.PI / 180f;
            s.Post.LightDirection = new Vector3(MathF.Cos(azimuth) * MathF.Cos(elevation), -MathF.Sin(elevation),
                MathF.Sin(azimuth) * MathF.Cos(elevation));
            s.Draw(ground, Matrix4x4.CreateTranslation(0f, -.1f, 0f), new Color(.4f, .45f, .35f, 1f), Material.None, false);
            foreach (InteriorVolume interior in inputs.Interiors)
            {
                Vector2 centre = (interior.Outline[0] + interior.Outline[2]) * .5f;
                s.Draw(box, Matrix4x4.CreateScale(24f, 9f, 24f)
                    * Matrix4x4.CreateTranslation(centre.X, 4.5f, centre.Y), new Color(.55f, .5f, .45f, 1f));
            }
            submittedTrees = 0;
            foreach (PropPlacement tree in inputs.Trees)
            {
                Matrix4x4 world = Matrix4x4.CreateScale(tree.Scale) * Matrix4x4.CreateRotationY(tree.Yaw)
                    * Matrix4x4.CreateTranslation(tree.X, tree.Y, tree.Z);
                s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateScale(.8f, 12f, .8f)
                    * Matrix4x4.CreateTranslation(0f, 6f, 0f) * world)
                { CutInsideInteriors = inputs.CutsFor(active), Tint = new Color(.35f, .22f, .12f, 1f) });
                for (int side = 0; side < 2; side++)
                    s.Draw(new RigidInstanceDraw(card, Matrix4x4.CreateRotationX(MathF.PI / 2f)
                        * Matrix4x4.CreateRotationY(side * MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(0f, 11f, 0f) * world)
                    { CutInsideInteriors = inputs.CutsFor(active) });
                submittedTrees++;
            }
        }

        var wall = Enum.GetValues<Mode>().ToDictionary(m => m, _ => new List<double>());
        var submit = Enum.GetValues<Mode>().ToDictionary(m => m, _ => new List<double>());
        output.WriteLine($"{Width}x{Height}, {Cascades} cascades at {Resolution}, 3600 trees with solid trunks and "
            + $"cutout cards, 49 box buildings over 500 m, sun advancing 0.2/60 degrees per frame, "
            + $"{Rounds} rounds, {Warm} warm and {Block} measured frames per block");
        for (int round = 0; round < Rounds; round++)
            foreach (Mode mode in OrderFor(round))
            {
                active = mode;
                scene.Post.Quality.Shadows.Mode = mode == Mode.ShadowsOff ? ShadowMode.Off : ShadowMode.ShadowMap;
                scene.InteriorCut = inputs.FieldFor(mode);
                fixture.Frames(Warm, Draw);
                double submitted = 0;
                bool everyShadowRecorded = true;
                long start = Stopwatch.GetTimestamp();
                for (int frame = 0; frame < Block; frame++)
                {
                    fixture.Frames(1, Draw);
                    submitted += fixture.LastSubmitMilliseconds;
                    if (mode != Mode.ShadowsOff)
                        everyShadowRecorded &= !scene.ShadowPassSkippedLastFrame && scene.ShadowCasterCandidateCount > 0;
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds / Block;
                wall[mode].Add(elapsed);
                submit[mode].Add(submitted / Block);
                Assert.Equal(3600, submittedTrees);
                Assert.Same(inputs.FieldFor(mode), scene.InteriorCut);
                Assert.True(everyShadowRecorded, "the advancing sun must record non-empty shadow passes every frame");
                Assert.Equal(Resolution, scene.Post.Quality.Shadows.ShadowMapResolution);
                Assert.Equal(Cascades, scene.Post.Quality.Shadows.ShadowCascadeCount);
                Assert.Equal(mode == Mode.ShadowsOff ? ShadowMode.Off : ShadowMode.ShadowMap, scene.ResolvedShadowMode);
                Assert.Equal(Width, fixture.DisplayWidth);
                Assert.Equal(Height, fixture.DisplayHeight);
                output.WriteLine($"round {round + 1} {mode}: frame {F(elapsed)} ms, submit and drain {F(submitted / Block)} ms");
                if (mode != Mode.ShadowsOff)
                    output.WriteLine($"  candidates {scene.ShadowCasterCandidateCount}, per-cascade instances "
                        + $"[{string.Join(", ", Enumerable.Range(0, Cascades).Select(scene.ShadowCascadeCasterCount))}]");
            }
        foreach (Mode mode in Enum.GetValues<Mode>())
            output.WriteLine($"{mode}: median frame {F(Median(wall[mode]))} ms, "
                + $"median submit and drain {F(Median(submit[mode]))} ms, blocks {string.Join(" ", wall[mode].Select(F))}");
        PrintPair("shadow pass, mode 2 minus mode 1", wall[Mode.NoOptIn], wall[Mode.ShadowsOff]);
        PrintPair("routing cost, mode 3 minus mode 2", wall[Mode.EmptyField], wall[Mode.NoOptIn]);
        PrintPair("total cost, mode 4 minus mode 2", wall[Mode.VillageInteriors], wall[Mode.NoOptIn]);
    }

    void PrintPair(string label, List<double> mode, List<double> baseline)
    {
        double difference = Median(mode) - Median(baseline);
        double[] paired = mode.Zip(baseline, (m, b) => m - b).ToArray();
        output.WriteLine($"{label}: {F(difference)} ms, paired median {F(Median(paired))}, min {F(paired.Min())}, "
            + $"max {F(paired.Max())}, paired blocks {string.Join(" ", paired.Select(F))}, budget {F(BudgetMs)} ms "
            + $"({(difference <= BudgetMs ? "within" : "over")}, reported only)");
    }

    static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * .5;
    }

    static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    static byte[] LeafPixels()
    {
        var pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int at = (y * 8 + x) * 4;
                pixels[at] = 60; pixels[at + 1] = 140; pixels[at + 2] = 45;
                pixels[at + 3] = (byte)((x + y) % 3 == 0 ? 0 : 255);
            }
        return pixels;
    }
}
