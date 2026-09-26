using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="FrameView.RebasedTo"/> carries a previous frame's view across a render origin step as <c>T(d) * M</c>
/// (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2). The step is exact, rows 1 to 3 of every rebased
/// matrix are untouched, and a still scene read across a 128 m step shows no motion beyond the float32 rounding of the
/// translation row.
/// </summary>
public sealed class FrameViewRebaseTests
{
    const int W = 1600, H = 900;

    // Eye and target near a 128 m frame boundary, on the side where subtracting either candidate origin shrinks every
    // coordinate, so both frames' reductions are exact (the WorldFrame lemma) and only the rebase itself can round.
    static (Vector3 Eye, Vector3 Target, Vector3 Step) Case(bool alongZ) => alongZ
        ? (new Vector3(12.75f, 9.5f, 70.25f), new Vector3(3.25f, 0.5f, 66.5f), new Vector3(0f, 0f, 128f))
        : (new Vector3(70.25f, 9.5f, 12.75f), new Vector3(66.5f, 0.5f, 3.25f), new Vector3(128f, 0f, 0f));

    static FrameView Latch(Vector3 eye, Vector3 target, Vector3 origin, bool perspective, long frameIndex)
    {
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, (float)W / H, 0.1f, 500f)
            : Matrix4x4.CreateOrthographic(17.75f, 10f, 0.1f, 200f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye - origin, target - origin, Vector3.UnitY);
        Matrix4x4 absolute = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * projection;
        return new FrameView(view, projection, view * projection, absolute, origin, W, H, frameIndex,
            TemporalJitter.Offset(frameIndex, TemporalJitter.NativePhaseCount));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AStillSceneAcrossA128MetreStepShowsNoMotion(bool perspective, bool alongZ)
    {
        (Vector3 eye, Vector3 target, Vector3 step) = Case(alongZ);
        FrameView previous = Latch(eye, target, Vector3.Zero, perspective, 1);
        FrameView current = Latch(eye, target, step, perspective, 2);
        FrameView rebased = previous.RebasedTo(current.RenderOrigin);

        Assert.Equal(step, rebased.RenderOrigin);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 4; column++)
                Assert.Equal(current.ViewProjection[row, column], rebased.ViewProjection[row, column]);
        foreach (Vector3 offset in new[]
        {
            Vector3.Zero, new Vector3(4.5f, 0f, -3.25f), new Vector3(-6.25f, 2.5f, 5.5f), new Vector3(2.75f, -0.5f, 1.25f),
        })
        {
            Vector3 local = target + offset - step;   // the still point in the current render frame
            float motion = Vector2.Distance(TemporalAssert.Uv(local, current.ViewProjection),
                TemporalAssert.Uv(local, rebased.ViewProjection));
            Assert.True(motion <= 1e-5f, $"a still point at {target + offset} moved {motion} UV across the step");
        }
    }

    [Fact]
    public void TheRebaseMovesOnlyTheTranslationRow()
    {
        (Vector3 eye, Vector3 target, Vector3 step) = Case(alongZ: false);
        FrameView previous = Latch(eye, target, Vector3.Zero, perspective: true, 1);
        FrameView rebased = previous.RebasedTo(step);
        foreach ((Matrix4x4 before, Matrix4x4 after) in new[]
        {
            (previous.ViewProjection, rebased.ViewProjection), (previous.View, rebased.View),
        })
        {
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 4; column++)
                    Assert.Equal(before[row, column], after[row, column]);
            for (int column = 0; column < 4; column++)
            {
                double expected = before[3, column] + (double)step.X * before[0, column]
                    + (double)step.Y * before[1, column] + (double)step.Z * before[2, column];
                Assert.Equal(expected, after[3, column], 1e-3);
            }
        }
        TemporalAssert.BitIdentical(previous.Projection, rebased.Projection, "Projection");
        TemporalAssert.BitIdentical(previous.AbsoluteViewProjection, rebased.AbsoluteViewProjection, "AbsoluteViewProjection");
        Assert.Equal(previous.JitterPixels, rebased.JitterPixels);
        Assert.Equal((previous.FrameIndex, previous.Width, previous.Height), (rebased.FrameIndex, rebased.Width, rebased.Height));
    }

    [Fact]
    public void AZeroStepReturnsTheSnapshotBitForBit()
    {
        (Vector3 eye, Vector3 target, Vector3 step) = Case(alongZ: false);
        FrameView view = Latch(eye, target, step, perspective: true, 3);
        FrameView same = view.RebasedTo(step);
        TemporalAssert.BitIdentical(view.View, same.View, "View");
        TemporalAssert.BitIdentical(view.ViewProjection, same.ViewProjection, "ViewProjection");
        TemporalAssert.BitIdentical(view.JitteredViewProjection, same.JitteredViewProjection, "JitteredViewProjection");
    }
}
