using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    [GpuFact]
    public void Held_gpu_transition_preserves_unrelated_actor_pixels_and_background_history()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.RenderScale = RenderScale.MatchViewport;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.CameraOverride = new HistoryCamera(width: 20);
        h.Scene.CoverageDiagnosticCompletionHeldForTests = true;
        using var other = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        other.ReserveSupports([h.Support]);
        var actor = MotionKey.From(20);
        var world = Matrix4x4.CreateTranslation(6, 0, 0);
        Draw(h); DrawActor(h, other, actor, 1, world); h.Render(200, 100);
        Draw(h, recession: .0001f); var otherFrame = DrawActor(h, other, actor, 1, world); h.Render(200, 100);
        Assert.False(h.Scene.TryReadCoverageOutcome(out _));
        float[] mask = h.Mask();
        Assert.Contains(1f, mask); Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
        var motion = h.Scene.ReadMotionTargetForTests();
        float[] confidence = TemporalTextureIo.Read(h.NativeDevice, h.Scene.TemporalHistory.Confidence(h.Scene.TemporalHistory.WriteIndex));
        int unrelated = 0, rejected = 0, accumulated = 0;
        for (int y = 30; y < 70; y++)
            for (int x = 140; x < 180; x++)
            {
                int pixel = y * 200 + x;
                if (MotionMath.IsBackground(motion.Motion[pixel])) continue;
                unrelated++;
                if (mask[pixel] != 0) rejected++;
                if (confidence[pixel * 2] > 1f / 15f + .001f) accumulated++;
            }
        Assert.True(unrelated > 0); Assert.Equal(0, rejected); Assert.True(accumulated > 0);
        Assert.True(h.Scene.TemporalHistory.IsValid);
        var draws = h.NativeDraws.Select(draw => new
        {
            draw.DrawWords,
            draw.Surface.CurrentBase,
            draw.Surface.PreviousBase,
            Header = CoverageSelectedReadback.Read(h.NativeDevice, draw.Surface.Current)
                .AsSpan((int)draw.Surface.CurrentBase, 16).ToArray(),
        }).ToArray();
        Assert.Equal(1u, draws[0].Header[15]); Assert.Equal(0u, draws[1].Header[15]);
        Assert.Equal(otherFrame.Preparation!.Actor.Value, (ulong)draws[1].Header[4] | (ulong)draws[1].Header[5] << 32);
        CoverageHistoryEvidence.Write(h.NativeDevice, "unrelated-actor", new
        {
            Width = 200,
            Height = 100,
            ActualBoundDraws = draws,
            ActualMask = mask,
            ActualUnrelatedActorPixelCount = unrelated,
            ActualUnrelatedActorRejectedPixelCount = rejected,
            ActualUnrelatedActorAccumulatedConfidencePixelCount = accumulated,
            ActualBackgroundCornerRejectedPixelCount = (mask[0] != 0 ? 1 : 0) + (mask[^1] != 0 ? 1 : 0),
        });
    }
}
