using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The scene's history advance (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2): once per frame, on
/// the frame's first render, with the previous view rebased to this frame's render origin, and nothing carried while
/// temporal rendering is off.
/// </summary>
public sealed class TemporalHistoryWiringTests
{
    [Fact]
    public void TheFirstTemporalFrameHasNoPreviousAndTheSecondReadsTheFirst()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;

        rig.Frame();
        Assert.Null(scene.PreviousFrameView);
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
        FrameView first = scene.CurrentFrameView;

        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);
        FrameView previous = TemporalAssert.Previous(scene);
        Assert.Equal(first.FrameIndex, previous.FrameIndex);
        Assert.Equal(first.JitterPixels, previous.JitterPixels);
        TemporalAssert.BitIdentical(first.ViewProjection, previous.ViewProjection, "the previous view-projection");
    }

    [Fact]
    public void ASecondRenderInTheSameFrameAdvancesNothing()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        FrameView first = scene.CurrentFrameView;
        long index = first.FrameIndex;
        Vector2 jitter = first.JitterPixels;
        long previousIndex = TemporalAssert.Previous(scene).FrameIndex;

        rig.Render(HeadlessSceneRig.Width / 2, HeadlessSceneRig.Height / 2);   // an offscreen capture at another size
        Assert.Equal((HeadlessSceneRig.Width / 2, HeadlessSceneRig.Height / 2),
            (scene.CurrentFrameView.Width, scene.CurrentFrameView.Height));   // the capture latched its own matrices
        Assert.Equal(index, scene.CurrentFrameView.FrameIndex);
        Assert.Equal(jitter, scene.CurrentFrameView.JitterPixels);
        Assert.True(scene.TemporalHistory.IsValid);
        Assert.Equal(previousIndex, TemporalAssert.Previous(scene).FrameIndex);

        rig.Frame();
        // The frame's first render is history, not its capture. The origin is unchanged, so the rebase is bit exact.
        FrameView previous = TemporalAssert.Previous(scene);
        Assert.Equal(index, previous.FrameIndex);
        Assert.Equal((HeadlessSceneRig.Width, HeadlessSceneRig.Height), (previous.Width, previous.Height));
        TemporalAssert.BitIdentical(first.ViewProjection, previous.ViewProjection, "the previous view-projection");
    }

    [Fact]
    public void AnInvalidationAfterTheAdvanceDropsThePreviousView()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        Assert.NotNull(scene.PreviousFrameView);

        // A history owner may drop the history later in the frame, after the advance set the previous view.
        scene.TemporalHistory.Invalidate(TemporalResetReason.Resize);
        Assert.Null(scene.PreviousFrameView);
    }

    [Fact]
    public void ABeginWithNoRenderKeepsTheLastRenderedFrameAsPrevious()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        long rendered = scene.CurrentFrameView.FrameIndex;
        scene.Begin();   // a frame that never rendered
        rig.Frame();
        Assert.Equal(rendered + 2, scene.CurrentFrameView.FrameIndex);
        Assert.Equal(rendered, TemporalAssert.Previous(scene).FrameIndex);
        Assert.True(scene.TemporalHistory.IsValid);
    }

    [Fact]
    public void AnOriginStepRebasesThePreviousViewSoAStillSceneShowsNoMotion()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        // The iso eye sits about 31.6 m out along x from the target, so both origins below shrink its x on subtraction
        // and the reductions are exact.
        scene.Camera.Target = new Vector3(70.5f, 0f, 10.25f);
        scene.RenderOrigin = Vector3.Zero;
        rig.Frame();
        scene.RenderOrigin = new Vector3(128f, 0f, 0f);
        rig.Frame();

        FrameView current = scene.CurrentFrameView;
        FrameView previous = TemporalAssert.Previous(scene);
        Assert.Equal(new Vector3(128f, 0f, 0f), current.RenderOrigin);
        Assert.Equal(current.RenderOrigin, previous.RenderOrigin);
        foreach (Vector3 world in new[]
        {
            new Vector3(70.5f, 0f, 10.25f), new Vector3(74f, 0.5f, 6f), new Vector3(66.25f, 1.5f, 14.75f), new Vector3(72.75f, 0f, 12.5f),
        })
        {
            Vector3 local = world - current.RenderOrigin;
            float motion = Vector2.Distance(TemporalAssert.Uv(local, current.ViewProjection),
                TemporalAssert.Uv(local, previous.ViewProjection));
            Assert.True(motion <= 1e-5f, $"a still point at {world} moved {motion} UV across the origin step");
        }
    }

    [Fact]
    public void WithTemporalOffTheHistoryStaysInvalidAndTurningItOnStartsFromScratch()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();
        rig.Frame();
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Null(scene.PreviousFrameView);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);

        scene.ForceTemporalForTests = true;
        rig.Frame();
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
        Assert.Null(scene.PreviousFrameView);
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);

        scene.ForceTemporalForTests = false;
        rig.Frame();
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Null(scene.PreviousFrameView);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
    }
}
