using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>How a bounded transition stream is drawn. The two host-only streams are current-surface controls for the
/// rejection mask: the host alone at each frame's pose and jitter, in the frame's own coverage identity or, on a
/// transition frame, in the identity it replaced.</summary>
internal enum CoverageStream { Accumulated, CurrentOnly, HostBefore, HostAfter }

public sealed partial class CoverageGpuScene
{
    static readonly string[] TransitionNames = ["uncovered", "equip", "steady", "blended-motion", "replacement",
        "post-replacement-steady", "refusal", "return", "unequip", "reequip", "reset", "equipment-fade",
        "ordinary-dissolve", "complementary-dissolve", "inverted-shadow-dissolve", "gone"];
    static bool CoveredAt(int frame) => frame is not (0 or 8);
    static bool RefusedAt(int frame) => frame == 6;
    static bool TransitionAt(int frame) => frame is 1 or 4 or 6 or 7 or 8 or 9 or 10 or 15;
    // Frames whose host coverage identity changes, where the host-only controls must see a moved surface.
    static bool HostChangeAt(int frame) => frame is 1 or 6 or 7 or 8 or 9;
    static bool PlateVisibleAt(int frame) => frame is 1 or 2 or 3 or 4 or 5 or 7 or 9 or 10 or 11;

    internal void AssertTransitions(bool split, bool upscale)
    {
        var actual = TransitionSequence(split, upscale, CoverageStream.Accumulated);
        Dispose();
        List<CoverageCapture> expected;
        Dictionary<int, CoverageHostFootprint> before, after;
        using (var reference = new CoverageGpuScene()) expected = reference.TransitionSequence(split, upscale, CoverageStream.CurrentOnly);
        using (var control = new CoverageGpuScene()) before = control.HostFootprints(split, upscale, CoverageStream.HostBefore);
        using (var control = new CoverageGpuScene()) after = control.HostFootprints(split, upscale, CoverageStream.HostAfter);
        CoverageLocalHistory? history = null;
        for (int i = 0; i < actual.Count; i++)
        {
            CoveragePixelProof.EqualTarget(actual[i].Depth, expected[i].Depth, CoveragePixelProof.DepthTolerance, $"transition{i} depth");
            CoveragePixelProof.EqualTarget(actual[i].Normal, expected[i].Normal, CoveragePixelProof.NormalTolerance, $"transition{i} normal");
            CoveragePixelProof.EqualTarget(actual[i].Cascades!, expected[i].Cascades!, CoveragePixelProof.DepthTolerance, $"transition{i} cascades");
            if (actual[i].TransientPoints is { } point)
                CoveragePixelProof.EqualTarget(point, expected[i].TransientPoints!, CoveragePixelProof.DepthTolerance, $"transition{i} point");
            else Assert.Null(expected[i].TransientPoints);
            // Empty groups do not consume their retained outline textures. The composed endpoint is checked in the stream.
            if (i != 15)
            {
                CoveragePixelProof.EqualTarget(actual[i].FullOutline!, expected[i].FullOutline!, 0, $"transition{i} full outline");
                CoveragePixelProof.EqualTarget(actual[i].VisibleOutline!, expected[i].VisibleOutline!, CoveragePixelProof.NormalTolerance, $"transition{i} visible outline");
            }
            if (TransitionAt(i)) history = null;
            history = AssertCurrentColour(actual[i], expected[i], requireHost: i != 15,
                requirePlate: PlateVisibleAt(i), history).History;
            if (TransitionAt(i)) AssertMaskComplete(before[i], after[i], actual[i], HostChangeAt(i), negative: i == 1, removed: i == 15);
            if (i == 1) CoverageHistoryNegative.AssertPartialSkin(actual[i], expected[i], before[i]);
            if (i == 15)
            {
                CoveragePixelProof.EqualTarget(actual[i].ResolvedColour!, expected[i].ResolvedColour!,
                    CoverageGhostEnvelope.Tolerance, "fully dissolved current resolve");
                Assert.True(actual[i].Colour.Zip(expected[i].Colour, (a, b) => Math.Abs(a - b)).Max() <= 1,
                    $"Fully dissolved output retains previous colour: {actual[i].Artifact}");
            }
        }
    }

    // The accumulated stream's resolve takes the current-only control's input, so its output must lie in that input's
    // reconstruction envelope at every pixel.
    static CoverageGhostEnvelope.Result AssertCurrentColour(CoverageCapture actual, CoverageCapture expected,
        bool requireHost, bool requirePlate, CoverageLocalHistory? history = null)
    {
        CoveragePixelProof.EqualTarget(actual.ReconstructionColour!, expected.ReconstructionColour!,
            CoveragePixelProof.OpaqueColourTolerance, $"{actual.Artifact} current colour");
        Assert.Equal(expected.ResolveJitter, actual.ResolveJitter);
        var inputs = new CoverageHistoryInputs(history, actual.Motion!.Value, actual.ResolveUniforms, actual.RejectionMask);
        var result = CoverageGhostEnvelope.Assert(actual.ResolvedColour!, actual.ResolvedState, expected.ReconstructionColour!,
            expected.Depth, actual.ResolveJitter, actual.Artifact, requireHost, requirePlate, inputs, expected.ResolvedColour);
        if (actual.Artifact.EndsWith("-14-inverted-shadow-dissolve", StringComparison.Ordinal)
            || actual.Artifact.EndsWith("-3-blended-motion", StringComparison.Ordinal))
            CoverageHistoryNegative.AssertWidest(result, actual, expected, inputs);
        return result;
    }

    static void AssertMaskComplete(CoverageHostFootprint before, CoverageHostFootprint after, CoverageCapture actual,
        bool hostChange, bool negative, bool removed = false)
    {
        var mask = actual.RejectionMask!;
        var complete = CoverageMaskCompleteness.Assert(before, after, mask, Width, Height, actual.ResolveJitter, actual.Artifact);
        if (removed) Assert.True(complete.RemovedWitnesses > 100 && complete.EdgeWitnesses > 0,
            $"Removed host footprint has no body or edge witness: {actual.Artifact}");
        if (!hostChange) return;
        Assert.True(complete.SurfaceWitnesses > 0 && complete.EdgeWitnesses > 0,
            $"Host change footprint has no surface or edge witness: {actual.Artifact} surface={complete.SurfaceWitnesses} edge={complete.EdgeWitnesses}");
        if (!negative) return;
        // A real mask with one required collar texel cleared must fail the same completeness assertion.
        var (x, y) = complete.FirstSurface!.Value;
        var (cx, cy) = CoverageGhostEnvelope.CentreTexel(x, y, Width, Height, mask.Width, mask.Height, actual.ResolveJitter);
        var values = (byte[])mask.Values.Clone();
        values[cy * mask.Width + cx] = 0;
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => CoverageMaskCompleteness.Assert(before, after,
            mask with { Values = values }, Width, Height, actual.ResolveJitter, actual.Artifact + "-incomplete-negative"));
        Assert.StartsWith("Incomplete coverage mask", failure.Message);
    }

    void DriveTransitions(bool split, bool upscale, CoverageStream stream, Action<int, byte[]> observe)
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
        for (int frame = 0; frame < TransitionNames.Length; frame++)
        {
            int identity = stream == CoverageStream.HostBefore && TransitionAt(frame) ? frame - 1 : frame;
            bool covered = CoveredAt(identity);
            bool refusal = RefusedAt(identity);
            if (frame == 10 || stream != CoverageStream.Accumulated) scene.CameraCut();
            float dissolve = frame >= 12 ? frame == 15 ? 1 : .5f : 0;
            if (stream == CoverageStream.HostBefore && frame == 15) dissolve = .5f;
            var current = row with { EquipmentFade = frame == 11 ? .5f : dissolve };
            Matrix4x4 world = Matrix4x4.CreateTranslation(128 + (frame == 3 ? .04f : 0), 0, 0);
            bool accumulated = stream == CoverageStream.Accumulated;
            int route = !covered ? 0 : accumulated ? 1 : refusal ? 0 : 2;
            bool equipment = (accumulated || stream == CoverageStream.CurrentOnly) && covered && !refusal;
            byte[] colour = Fixture.Frame((s, _) =>
            {
                s.AddLight(new Vector3(128, 0, 0), KhaozEngine.Primitives.Color.White, 6, 1, LightShadow.Static(8001));
                actor.Draw(current, route, world, instance: identity >= 4 ? 2ul : 1ul, submitEquipment: equipment,
                    dissolve: dissolve, complement: frame == 13, invertShadow: frame == 14, blended: frame == 3);
            });
            observe(frame, colour);
        }
    }

    Dictionary<int, CoverageHostFootprint> HostFootprints(bool split, bool upscale, CoverageStream stream)
    {
        var footprints = new Dictionary<int, CoverageHostFootprint>();
        DriveTransitions(split, upscale, stream, (frame, _) =>
        {
            if (TransitionAt(frame)) footprints[frame] = HostFootprint($"coverage-{captureId}-{stream}-split{split}-upscale{upscale}-{frame}");
        });
        return footprints;
    }

    CoverageHostFootprint HostFootprint(string artifact)
    {
        var scene = Fixture.Scene;
        var footprint = new CoverageHostFootprint(scene.ReadCoverageReconstructionColourForTests(),
            scene.ReadCoverageDepthForTests(), ResolveJitter());
        CoverageArtifacts.WriteRaw($"{artifact}.host-colour.f32", footprint.Colour.Values);
        CoverageArtifacts.WriteRaw($"{artifact}.host-depth.f32", footprint.Depth.Values);
        CoverageArtifacts.WriteJson($"{artifact}.host-footprint.json", new
        {
            Artifact = artifact,
            footprint.Colour.Width,
            footprint.Colour.Height,
            JitterX = footprint.Jitter.X,
            JitterY = footprint.Jitter.Y,
            Backend = Fixture.Device.Backend.ToString(),
            Device = Fixture.Device.Capabilities.DeviceName,
        });
        return footprint;
    }

    Vector2 ResolveJitter()
    {
        var jitter = Fixture.Scene.TemporalResolveRendererForTests!.LastUniforms.Jitter;
        return new(jitter.X, jitter.Y);
    }

    List<CoverageCapture> TransitionSequence(bool split, bool upscale, CoverageStream stream)
    {
        var scene = Fixture.Scene;
        var actor = Actors[0];
        var captures = new List<CoverageCapture>();
        DriveTransitions(split, upscale, stream, (frame, colour) =>
        {
            bool accumulated = stream == CoverageStream.Accumulated;
            if (accumulated)
            {
                if (CoveredAt(frame) && !RefusedAt(frame)) actor.AssertCovered();
                if (RefusedAt(frame)) Assert.Equal(CoveragePresentation.UncoveredFallback, scene.GetCoverageOutcome(actor.Frame).Presentation);
                if (TransitionAt(frame))
                {
                    AssertRejectedConfidence();
                    CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(this);
                }
                if (frame == 3)
                    Assert.Contains(scene.ReadMotionTargetForTests().Motion,
                        v => !MotionMath.IsBackground(v) && v.LengthSquared() > 1e-8f);
            }
            var capture = TemporalCapture(colour, actor.Signature, accumulated,
                $"coverage-{captureId}-{captureNumber++}-temporal-control{!accumulated}-split{split}-upscale{upscale}-{frame}-{TransitionNames[frame]}");
            CoverageArtifacts.Write(capture);
            captures.Add(capture);
            AssertComposedOutline(frame, colour, capture);
        });
        return captures;
    }

    CoverageCapture TemporalCapture(byte[] colour, string signature, bool readMask, string artifact)
    {
        var scene = Fixture.Scene;
        var history = scene.TemporalHistory;
        var outline = scene.ReadCoverageOutlinesForTests();
        return new CoverageCapture(colour, scene.ReadCoverageDepthForTests(), scene.ReadCoverageNormalForTests(),
            scene.ReadCoverageCascadesForTests(), scene.ReadCoveragePointsForTests(false), scene.ReadCoveragePointsForTests(true),
            outline.Full, outline.Visible, signature, Fixture.Device.Backend, Fixture.Device.Capabilities.DeviceName, artifact)
        {
            Motion = scene.ReadMotionTargetForTests(),
            FrameStats = scene.LastFrameStats,
            Temporal = scene.LastTemporalDiagnostics,
            Jitter = scene.CurrentFrameView.JitterPixels,
            ReconstructionColour = scene.ReadCoverageReconstructionColourForTests(),
            ResolvedColour = HistoryTarget(history.Color(history.WriteIndex)),
            ResolvedState = HistoryTarget(history.Confidence(history.WriteIndex)),
            ResolveJitter = ResolveJitter(),
            ResolveUniforms = scene.TemporalResolveRendererForTests!.LastUniforms,
            RejectionMask = readMask ? ReadMaskReadback() : null,
            OutlineGroupsRendered = scene.TargetOutlineGroupsRenderedForTests,
        };
    }

    CoverageTargetReadback HistoryTarget(IGpuTexture texture)
    {
        float[] values = TemporalTextureIo.Read(Fixture.Device, texture);
        int pixels = (int)(texture.Width * texture.Height);
        return new((int)texture.Width, (int)texture.Height, values.Length / pixels, values);
    }

    // Composed target outlines are final-output evidence, distinct from the retained mask textures. The post chain
    // here is the temporal sharpen alone (an AA-off capture's display equals its scene colour byte for byte), so the
    // display before composition is TemporalSharpenMath over the resolve output. A display pixel lifted toward the
    // white outline by more than one byte in every channel was composed this frame. Every undissolved frame renders
    // its one group and shows such pixels. The dissolved endpoint renders none and equals the sharpened resolve.
    void AssertComposedOutline(int frame, byte[] colour, CoverageCapture capture)
    {
        var resolved = capture.ResolvedColour!;
        float sharpness = Fixture.Scene.Post.Temporal.ResolvedSharpness;
        Vector3 At(int x, int y)
        {
            int i = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * resolved.Channels;
            return new(resolved.Values[i], resolved.Values[i + 1], resolved.Values[i + 2]);
        }
        int composed = 0, strongest = 0, worst = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Vector3 expected = TemporalSharpenMath.Sharpen(At(x, y - 1), At(x - 1, y), At(x, y), At(x + 1, y),
                    At(x, y + 1), sharpness) * 255;
                int i = y * Width + x, lift = int.MaxValue, delta = 0;
                for (int c = 0; c < 3; c++)
                {
                    int value = colour[i * 4 + c] - (int)MathF.Round(c == 0 ? expected.X : c == 1 ? expected.Y : expected.Z);
                    lift = Math.Min(lift, value);
                    delta = Math.Max(delta, Math.Abs(value));
                }
                if (lift > 1) composed++;
                strongest = Math.Max(strongest, lift);
                worst = Math.Max(worst, delta);
            }
        Console.WriteLine($"coverage composed-outline={capture.Artifact} groups={capture.OutlineGroupsRendered} "
            + $"sharpness={sharpness} composed={composed} strongestLift={strongest} worstDelta={worst}");
        if (frame < 12)
            Assert.True(capture.OutlineGroupsRendered == 1 && composed > 0,
                $"No composed outline witness: {capture.Artifact} groups={capture.OutlineGroupsRendered} composed={composed}");
        if (frame == 15)
        {
            Assert.Equal(0, capture.OutlineGroupsRendered);
            Assert.True(worst <= 1, $"Dissolved display differs from its sharpened resolve output by {worst}: {capture.Artifact}");
        }
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

    CoverageMaskReadback ReadMaskReadback()
    {
        var texture = Fixture.Scene.CoverageTransitionForTests.Texture!;
        return new((int)texture.Width, (int)texture.Height, ReadMask());
    }
}
