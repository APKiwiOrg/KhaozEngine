using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed partial class CoverageComputeHistoryGpuTests
{
    internal static SkinnedCoverageFrame Draw(CoverageSceneFixture h, float weight = 1, Matrix4x4? model = null, float? recession = null)
    {
        h.Begin();
        var frame = h.Stage(model, weight: weight, recession: recession);
        for (int slice = 0; slice < h.Host.Length; slice++) h.Scene.DrawSkinned(h.HostDraw(frame, slice: slice), h.Palette);
        h.SubmitEquipment();
        return frame;
    }

    [GpuFact]
    public void Held_readback_refusal_and_recovery_seed_history_locally()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.Camera.OrthoSize = 10;
        h.Scene.CoverageDiagnosticCompletionHeldForTests = true;
        var rows = new List<object>();
        uint[]? previousCurrent = null;
        for (int frame = 0; frame < 5; frame++)
        {
            var submitted = Draw(h, 1 - frame * .2f, recession: frame == 1 ? .0001f : null);
            h.Render(96, 72);
            Assert.True(submitted.Preparation!.MotionPresented);
            Assert.False(h.Scene.TryReadCoverageOutcome(out _));
            var draw = h.NativeDraws.First();
            var current = CoverageSelectedReadback.Read(h.NativeDevice, draw.Surface.Current);
            var committed = CoverageSelectedReadback.Read(h.NativeDevice, draw.Surface.Previous);
            Assert.Equal(frame < 3 ? 1u : 0u, current[draw.Surface.CurrentBase + 15]);
            Assert.Equal(frame == 1 ? 1u : 0u, current[draw.Surface.CurrentBase + 14]);
            if (previousCurrent is not null) Assert.Equal(previousCurrent, committed);
            Assert.Same(draw.Surface.Current, h.Scene.CoverageGpuBatchForTests!.Previous);
            var selected = VerifySelection(h, draw, out int mismatchWords);
            var mask = h.Mask();
            if (frame < 3)
            {
                Assert.Contains(1f, mask); Assert.Contains(0f, mask);
                Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
            }
            else Assert.All(mask, value => Assert.Equal(0f, value));
            var motion = h.Scene.ReadMotionTargetForTests();
            Assert.Contains(motion.Motion, value => !MotionMath.IsBackground(value));
            if (frame is 1 or 2)
                Assert.All(motion.Motion.Where(value => !MotionMath.IsBackground(value)), value => Assert.Equal(Vector2.Zero, value));
            rows.Add(new
            {
                Frame = frame + 1,
                Width = 96,
                Height = 72,
                Actor = submitted.Preparation.Actor.Value,
                draw.Surface.CurrentBase,
                draw.Surface.PreviousBase,
                submitted.Preparation.GpuReservation.SourceCount,
                draw.Surface.VertexCount,
                BoundDrawWords = draw.DrawWords,
                ActualCurrentBufferId = CoverageHistoryEvidence.BufferId(draw.Surface.Current),
                ActualPreviousBufferId = CoverageHistoryEvidence.BufferId(draw.Surface.Previous),
                SelectedCornerCount = selected.Length / 6,
                SelectionMismatchCount = mismatchWords,
                SelectedCornerWordsSha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(selected.AsSpan()))),
                RejectedPixelCount = mask.Count(value => value != 0),
                PreservedPixelCount = mask.Count(value => value == 0),
                NonBackgroundMotionCount = motion.Motion.Count(value => !MotionMath.IsBackground(value)),
                NonzeroMotionCount = motion.Motion.Count(value => !MotionMath.IsBackground(value) && value != Vector2.Zero),
                CurrentHeader = current.Take(16).ToArray(),
                PriorHeader = frame == 0 ? [] : committed.Take(16).ToArray(),
                ProductionSelectedCornerWords = selected,
                Mask = mask,
                Motion = motion.Motion.Select(v => new[] { v.X, v.Y }).ToArray(),
                ResolvedColour = TemporalTextureIo.Read(h.NativeDevice, h.Scene.TemporalHistory.Color(h.Scene.TemporalHistory.WriteIndex)),
                ResolvedConfidence = TemporalTextureIo.Read(h.NativeDevice, h.Scene.TemporalHistory.Confidence(h.Scene.TemporalHistory.WriteIndex))
            });
            previousCurrent = current;
        }
        Assert.True(h.LastCoverageFrame.Preparation!.MotionReusable);
        h.Scene.CoverageDiagnosticCompletionHeldForTests = false;
        var outcomes = new List<CoverageGpuOutcome>();
        while (h.Scene.TryReadCoverageOutcome(out var outcome)) outcomes.Add(outcome);
        Assert.Equal(new ulong[] { 1, 2, 3 }, outcomes.Select(o => o.FrameId));
        Assert.Equal(CoveragePresentation.Covered, outcomes[0].Outcome.Presentation);
        Assert.Equal(CoveragePresentation.UncoveredFallback, outcomes[1].Outcome.Presentation);
        Assert.Equal(CoveragePresentation.Covered, outcomes[2].Outcome.Presentation);
        CoverageHistoryEvidence.Write(h.NativeDevice, "held-refusal-recovery", new
        {
            HeldDiagnosticVisibility = true,
            NativeFenceHeld = false,
            HistoryCommittedWithoutDiagnosticSlot = true,
            DeliveredDiagnosticFrames = outcomes.Select(o => o.FrameId).ToArray(),
            Actual = rows,
        });
    }

    [GpuFact]
    public void Previous_final_positions_rebase_to_the_current_origin()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        h.NativeEquipmentShadowOnly = true;
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.RenderScale = RenderScale.MatchViewport;
        h.Scene.CameraOverride = new HistoryCamera();
        h.Scene.RenderOrigin = new(128, 0, 0);
        h.Palette[0] = h.Palette[1] = Matrix4x4.Identity;
        Draw(h, weight: 0, model: Matrix4x4.CreateTranslation(1.04f, 0, 0)); h.Render(96, 72);
        h.Scene.RenderOrigin = new(256, 0, 0);
        h.Palette[0] = h.Palette[1] = Matrix4x4.CreateTranslation(.125f, 0, 0);
        var next = Draw(h, 0, Matrix4x4.CreateTranslation(2, 0, 0)); h.Render(96, 72);
        Assert.True(next.Preparation!.MotionReusable);
        var actualDraw = Assert.Single(h.NativeDraws);
        Assert.Equal(new Vector3(1.04f - 256, 0, 0), actualDraw.PreviousModel.Translation);
        var selected = VerifySelection(h, actualDraw);
        var motion = h.Scene.ReadMotionTargetForTests().Motion.Where(v => !MotionMath.IsBackground(v)).ToArray();
        Assert.NotEmpty(motion);
        float expected = (float)(Half)((2 - 1.04f + .125f) / 8);
        float expectedY = MotionMath.UvMotion(Vector4.UnitW, Vector4.UnitW).Y;
        Assert.Equal(0x8000, BitConverter.HalfToUInt16Bits((Half)expectedY));
        ushort[] expectedWords = [BitConverter.HalfToUInt16Bits((Half)expected), BitConverter.HalfToUInt16Bits((Half)expectedY)];
        ushort[][] actualWords = motion.Select(v => new[] { BitConverter.HalfToUInt16Bits((Half)v.X), BitConverter.HalfToUInt16Bits((Half)v.Y) }).ToArray();
        Assert.All(actualWords, words => Assert.Equal(expectedWords, words));
        Assert.Contains(Enumerable.Range(0, selected.Length / 6), i => selected[i * 6] != selected[i * 6 + 3]);
        CoverageHistoryEvidence.Write(h.NativeDevice, "origin-motion", new
        {
            Width = h.Scene.CurrentFrameView.Width,
            Height = h.Scene.CurrentFrameView.Height,
            NonBackgroundMotionCount = actualWords.Length,
            ActualSelectedCornerWords = selected,
            ActualCurrentBufferId = CoverageHistoryEvidence.BufferId(actualDraw.Surface.Current),
            ActualPreviousBufferId = CoverageHistoryEvidence.BufferId(actualDraw.Surface.Previous),
            actualDraw.Surface.CurrentBase,
            actualDraw.Surface.PreviousBase,
            ReferenceModelTranslationDelta = 2 - 1.04f,
            ReferencePaletteTranslationDelta = .125f,
            ReferenceOrthographicWidth = 8,
            ExpectedReferenceMotionWords = expectedWords,
            ActualMotionWords = actualWords,
            StoredWordMismatchCount = actualWords.Count(words => !words.SequenceEqual(expectedWords)),
            ActualPreviousModelTranslation = new[] { actualDraw.PreviousModel.M41, actualDraw.PreviousModel.M42, actualDraw.PreviousModel.M43 },
        });
    }

    [GpuFact]
    public void Unpresented_or_other_view_surfaces_are_not_history()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Scene.CameraOverride = new IsoCamera3D();
        var other = Draw(h, .5f); h.Render();
        Assert.False(other.Preparation!.MotionReusable);
        Assert.True(other.Preparation.MotionPresented);
    }
}
