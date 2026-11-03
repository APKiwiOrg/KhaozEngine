using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class ShaderGatePixelIdentityGpuTests
{
    // #1680, #1678 and #1677. The candidate's G1 is on in both arms. Every gate gets its own off/on comparison.
    [GpuFact]
    public void Candidate_GPU_cuts_preserve_every_main_attachment_and_temporal_history_bit()
    {
        var off = CandidateRun(ShaderGateProbe.PointShadowZeroNdl, out var counts);
        var repeat = CandidateRun(ShaderGateProbe.PointShadowZeroNdl, out _);
        Assert.True(counts.ZeroSpecular > 0 && counts.PositiveSpecular > 0 && counts.Tile > 0);
        output.WriteLine($"Outside-shader reconstruction on the last frame: G5 zero-strength {counts.ZeroSpecular}, small-positive {counts.PositiveSpecular}, G6 repeated-slot ground {counts.Tile} fragments");
        output.WriteLine($"G4 exact sizes {W}x{H}, {W * H} accumulation fragments per frame. G6 has five active roles (0,0,1,0,0), three reuses per ground fragment, estimated {3 * counts.Tile} avoided evaluations on the last frame");
        CompareCandidate("repeat off", off, repeat);
        foreach (ShaderGateProbe gate in new[] { ShaderGateProbe.LanczosExactSizeReuse, ShaderGateProbe.ZeroSpecular, ShaderGateProbe.TileSlotReuse })
        {
            var on = CandidateRun(ShaderGateProbe.PointShadowZeroNdl | gate, out _);
            CompareCandidate(gate.ToString(), off, on);
        }
        var control = CandidateRun(ShaderGateProbe.PointShadowZeroNdl, out _, positiveStrength: 0f);
        int changed = Differing(off[^1]["hdr"], control[^1]["hdr"]);
        output.WriteLine($"Positive-specular control changed {changed} HDR bytes");
        Assert.True(changed > 0, "small positive specular must contribute to the rendered scene");
    }

    List<Dictionary<string, byte[]>> CandidateRun(ShaderGateProbe gates, out (int ZeroSpecular, int PositiveSpecular, int Tile) counts,
        float positiveStrength = 0.001f)
    {
        using var stage = new Stage(gates, 1);
        Scene3D scene = stage.Scene;
        scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        scene.Post.CelBands = 0;
        scene.Post.LightDirection = -Vector3.UnitY;
        scene.Post.LightColor = Color.White;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.4f;
        scene.Camera.AspectRatio = (float)W / H;
        scene.Camera.OrthoSize = 8f;
        scene.Camera.Target = Vector3.Zero;
        var first = CandidateLayer(140, 80, 45);
        first.Steep = new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 10, ToDegrees = 50 };
        var material = scene.LoadTileGroundMaterial(4, 4, new[] { first, CandidateLayer(40, 110, 65) }, baseSpecStrength: 0f);
        Vector3 normal = Vector3.Normalize(new Vector3(0.5f, 1f, 0.3f));
        var weights = new Vector4(0.2f);
        var slots = new Vector2(0, 0);
        var tangent = new Vector4(1, 0, 1, 1); // slots 2 and 3, jitter, overlay slot 0 + 1
        var vertices = new[]
        {
            new ModelVertex(new(-4, 0, -4), normal, weights, slots, tangent),
            new ModelVertex(new(4, 0, -4), normal, weights, slots, tangent),
            new ModelVertex(new(4, 0, 4), normal, weights, slots, tangent),
            new ModelVertex(new(-4, 0, 4), normal, weights, slots, tangent),
        };
        MeshHandle ground = scene.LoadMesh(new GltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 }), material);
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box());
        var frames = new List<Dictionary<string, byte[]>>();
        Attachments last = null!;
        for (int frame = 0; frame < Frames; frame++)
        {
            last = stage.Frame((s, _) =>
            {
                s.Draw(ground, Matrix4x4.Identity, Color.White, Material.None, castsShadows: false);
                s.Draw(box, Matrix4x4.CreateScale(3, 0.6f, 3) * Matrix4x4.CreateTranslation(-2, 0.3f, 0),
                    Color.White, Material.None, castsShadows: false);
                s.Draw(box, Matrix4x4.CreateScale(3, 0.6f, 3) * Matrix4x4.CreateTranslation(2, 0.3f, 0),
                    Color.White, Material.Shiny(positiveStrength, 8), castsShadows: false);
                s.AddLight(new Vector3(0, 4, 0), Color.White, 12, 2);
            });
            Assert.Equal((int)gates, scene.ConsumeAppliedProbeShaderGates());
            Assert.Equal(1, scene.ConsumeProbeExactSizeCondition());
            var parts = Parts.ToDictionary(p => p.Name, p => p.Read(last));
            TemporalHistory history = scene.TemporalHistory;
            parts.Add("accumulationColor", Raw(stage.Device, history.Color(history.WriteIndex)));
            parts.Add("accumulationConfidence", Raw(stage.Device, history.Confidence(history.WriteIndex)));
            parts.Add("historyDepth", Raw(stage.Device, history.PreviousDepth(history.WriteIndex)));
            var depth = ShaderGateDepthReadback.Read(stage.Device, scene.ProbeDepthStencilForTests);
            parts.Add("hardwareDepth", depth.Depth);
            parts.Add("hardwareStencil", depth.Stencil);
            frames.Add(parts);
        }
        Assert.True(Matrix4x4.Invert(scene.CurrentFrameView.JitteredViewProjection, out var inverse));
        counts = (0, 0, 0);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float depth = BitConverter.ToSingle(last.Depth, (y * W + x) * 4);
                if (depth >= 1f) continue;
                Vector4 p = Vector4.Transform(new Vector4((x + .5f) / W * 2 - 1, 1 - (y + .5f) / H * 2, depth, 1), inverse);
                float worldY = p.Y / p.W;
                if (worldY < .1f) counts.Tile++;
                else if (p.X < 0) counts.ZeroSpecular++;
                else counts.PositiveSpecular++;
            }
        return frames;
    }

    static TileGroundLayerImage CandidateLayer(byte r, byte g, byte b)
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(r + i % 7);
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return new() { AlbedoRgba = pixels, TilesPerMetre = .7f };
    }

    void CompareCandidate(string name, List<Dictionary<string, byte[]>> a, List<Dictionary<string, byte[]>> b)
    {
        Assert.Equal(Frames, a.Count);
        Assert.Equal(Frames, b.Count);
        int total = 0;
        foreach (string part in a[0].Keys)
        {
            int[] diffs = Enumerable.Range(0, Frames).Select(i => Differing(a[i][part], b[i][part])).ToArray();
            output.WriteLine($"{name} {part}, {a[0][part].Length} bytes/frame, differing {string.Join(',', diffs)}");
            total += diffs.Sum();
        }
        Assert.Equal(0, total);
    }
}
