using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageGpuScene
{
    internal void AssertTransitions(bool split, bool upscale)
    {
        var actual = TransitionSequence(split, upscale, control: false);
        Dispose();
        using var reference = new CoverageGpuScene();
        var expected = reference.TransitionSequence(split, upscale, control: true);
        for (int i = 0; i < actual.Count; i++)
        {
            CoveragePixelProof.EqualTarget(actual[i].Depth, expected[i].Depth, CoveragePixelProof.DepthTolerance, $"transition{i} depth");
            CoveragePixelProof.EqualTarget(actual[i].Normal, expected[i].Normal, CoveragePixelProof.NormalTolerance, $"transition{i} normal");
            CoveragePixelProof.EqualTarget(actual[i].Cascades!, expected[i].Cascades!, CoveragePixelProof.DepthTolerance, $"transition{i} cascades");
            if (actual[i].TransientPoints is { } point)
                CoveragePixelProof.EqualTarget(point, expected[i].TransientPoints!, CoveragePixelProof.DepthTolerance, $"transition{i} point");
            else Assert.Null(expected[i].TransientPoints);
            // Empty groups do not consume their retained outline textures. The opaque endpoint is checked below.
            if (i != 15)
            {
                CoveragePixelProof.EqualTarget(actual[i].FullOutline!, expected[i].FullOutline!, 0, $"transition{i} full outline");
                CoveragePixelProof.EqualTarget(actual[i].VisibleOutline!, expected[i].VisibleOutline!, CoveragePixelProof.NormalTolerance, $"transition{i} visible outline");
            }
            if (upscale && i == 3) CoveragePixelProof.AssertMixedReconstructionFootprint(expected[i], 113, 81);
            if (!upscale && i == 11) CoveragePixelProof.AssertMixedReconstructionFootprint(expected[i], 129, 75);
            CoveragePixelProof.AssertNoGhostColour(actual[i], expected[i]);
        }
    }

    List<CoverageCapture> TransitionSequence(bool split, bool upscale, bool control)
    {
        var row = new CoverageCase(0) { Shadows = true, StaticLight = true, Outlines = true };
        SetCase(row, true);
        Fixture.Scene.RenderOrigin = new(128, 0, 0);
        Fixture.Scene.Camera.Target = new(128, 0, 0);
        PrimePoints(row);
        var scene = Fixture.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = upscale ? TemporalUpscale.Performance : TemporalUpscale.Native;
        scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        var actor = Actors[0];
        string[] sequence = ["uncovered", "equip", "steady", "blended-motion", "replacement", "post-replacement-steady",
            "refusal", "return", "unequip", "reequip", "reset", "equipment-fade", "ordinary-dissolve",
            "complementary-dissolve", "inverted-shadow-dissolve", "gone"];
        var captures = new List<CoverageCapture>();
        for (int frame = 0; frame < sequence.Length; frame++)
        {
            bool covered = frame is not (0 or 8);
            bool refusal = frame == 6;
            bool transition = frame is 1 or 4 or 6 or 7 or 8 or 9 or 10;
            if (frame == 10 || control) scene.CameraCut();
            float dissolve = frame >= 12 ? frame == 15 ? 1 : .5f : 0;
            var current = row with { EquipmentFade = frame == 11 ? .5f : dissolve };
            Matrix4x4 world = Matrix4x4.CreateTranslation(128 + (frame == 3 ? .04f : 0), 0, 0);
            byte[] colour = Fixture.Frame((s, _) =>
            {
                s.AddLight(new Vector3(128, 0, 0), KhaozEngine.Primitives.Color.White, 6, 1, LightShadow.Static(8001));
                actor.Draw(current, covered ? control ? refusal ? 0 : 2 : 1 : 0, world,
                    instance: frame >= 4 ? 2ul : 1ul, submitEquipment: covered && !refusal,
                    dissolve: dissolve, complement: frame == 13, invertShadow: frame == 14, blended: frame == 3);
            });
            if (!control)
            {
                if (covered && !refusal) actor.AssertCovered();
                if (refusal) Assert.Equal(CoveragePresentation.UncoveredFallback, scene.GetCoverageOutcome(actor.Frame).Presentation);
                if (transition)
                {
                    AssertRejectedConfidence();
                    CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(this);
                }
                if (frame == 3)
                    Assert.Contains(scene.ReadMotionTargetForTests().Motion,
                        v => !MotionMath.IsBackground(v) && v.LengthSquared() > 1e-8f);
            }
            var outline = scene.ReadCoverageOutlinesForTests();
            var capture = new CoverageCapture(colour, scene.ReadCoverageDepthForTests(), scene.ReadCoverageNormalForTests(),
                scene.ReadCoverageCascadesForTests(), scene.ReadCoveragePointsForTests(false), scene.ReadCoveragePointsForTests(true),
                outline.Full, outline.Visible, actor.Signature, Fixture.Device.Backend,
                Fixture.Device.Capabilities.DeviceName,
                $"coverage-{captureId}-{captureNumber++}-temporal-control{control}-split{split}-upscale{upscale}-{frame}-{sequence[frame]}")
            { Motion = scene.ReadMotionTargetForTests(), FrameStats = scene.LastFrameStats, Temporal = scene.LastTemporalDiagnostics, Jitter = scene.CurrentFrameView.JitterPixels, ReconstructionColour = scene.ReadCoverageReconstructionColourForTests() };
            CoverageArtifacts.Write(capture);
            captures.Add(capture);
            if (frame == 15)
            {
                int white = Enumerable.Range(0, Width * Height).Count(i => colour[i * 4] > 230
                    && colour[i * 4 + 1] > 230 && colour[i * 4 + 2] > 230);
                Assert.Equal(0, white);
            }
        }
        return captures;
    }

    void AssertRejectedConfidence()
    {
        byte[] mask = ReadMask();
        Assert.Contains((byte)255, mask);
        var scene = Fixture.Scene;
        var texture = scene.CoverageTransitionForTests.Texture!;
        var history = scene.TemporalHistory;
        float[] state = TemporalTextureIo.Read(Fixture.Device, history.Confidence(history.WriteIndex));
        int rejected = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int mx = Math.Clamp((int)((x + .5f) * texture.Width / Width + scene.CurrentFrameView.JitterPixels.X), 0, (int)texture.Width - 1);
                int my = Math.Clamp((int)((y + .5f) * texture.Height / Height + scene.CurrentFrameView.JitterPixels.Y), 0, (int)texture.Height - 1);
                if (mask[my * (int)texture.Width + mx] == 0) continue;
                Assert.InRange(state[(y * Width + x) * 2], 0, 1f / 15f + .001f);
                rejected++;
            }
        Assert.True(rejected > 0);
    }

    internal byte[] ReadMask()
    {
        var texture = Fixture.Scene.CoverageTransitionForTests.Texture!;
        var device = Fixture.Device;
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width,
            texture.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "Coverage.ReadMask")) commands.CopyTexture(texture, staging);
        device.Submit(commands);
        device.WaitForIdle();
        var map = device.Map(staging, GpuMapMode.Read);
        var pixels = new byte[texture.Width * texture.Height];
        try
        {
            for (int y = 0; y < texture.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(map.Data + (int)(y * map.RowPitch), pixels,
                    y * (int)texture.Width, (int)texture.Width);
        }
        finally { device.Unmap(staging); }
        return pixels;
    }
}
