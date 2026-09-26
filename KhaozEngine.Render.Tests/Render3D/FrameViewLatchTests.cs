using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <c>Scene3D.LatchFrameView</c>: one snapshot per render, latched after <c>EnsureSize</c>, holding the camera's own
/// matrices bit for bit with temporal rendering off, and the Halton jitter only while temporal rendering is forced on.
/// </summary>
public sealed class FrameViewLatchTests
{
    [Fact]
    public void WithTemporalOffTheSnapshotIsTheCamerasOwnMatricesBitForBit()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.Camera.Target = new Vector3(1000f, 0f, 1000f);   // far enough out that the render origin is active
        rig.Frame();

        FrameView view = scene.CurrentFrameView;
        Assert.True(scene.RenderOriginActive);
        TemporalAssert.BitIdentical(scene.Camera.View, view.View, "View");
        TemporalAssert.BitIdentical(scene.Camera.Projection, view.Projection, "Projection");
        TemporalAssert.BitIdentical(scene.Camera.ViewProjection, view.ViewProjection, "ViewProjection");
        TemporalAssert.BitIdentical(scene.Camera.AbsoluteViewProjection, view.AbsoluteViewProjection, "AbsoluteViewProjection");
        TemporalAssert.BitIdentical(view.ViewProjection, view.JitteredViewProjection, "JitteredViewProjection");
        TemporalAssert.BitIdentical(view.Projection, view.JitteredProjection, "JitteredProjection");
        Assert.Equal(scene.RenderOrigin.GetValueOrDefault(), view.RenderOrigin);
        Assert.Equal(Vector2.Zero, view.JitterPixels);
        Assert.True(view.IsOrthographic);
    }

    [Fact]
    public void TheSnapshotIsLatchedAfterTheInternalSizeAndTheCameraAspectAreFinal()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();
        rig.Frame(96, 48);

        FrameView view = scene.CurrentFrameView;
        Assert.Equal((96, 48), (view.Width, view.Height));
        Assert.Equal((scene.RenderTargetWidth, scene.RenderTargetHeight), (view.Width, view.Height));
        Assert.Equal(2f, scene.Camera.AspectRatio);
        TemporalAssert.BitIdentical(scene.Camera.Projection, view.Projection, "the projection at the new aspect");
    }

    [Fact]
    public void AFixedInternalTargetIsLatchedAtItsOwnSizeNotTheViewports()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.RenderScale = RenderScale.FixedInternal;
        rig.Scene.Post.RenderWidth = 320;
        rig.Scene.Post.RenderHeight = 180;
        rig.Frame();
        Assert.Equal((320, 180), (rig.Scene.CurrentFrameView.Width, rig.Scene.CurrentFrameView.Height));
    }

    [Fact]
    public void AnOverrideThatCannotTakeAnOriginGetsItComposedOntoItsView()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.Camera.Target = new Vector3(1000f, 0f, 1000f);
        var plain = new PlainCamera();
        // Swapped in after Begin latched a nonzero origin: the fallback FrameViewProjection applies.
        rig.Frame(s => s.CameraOverride = plain);

        FrameView view = scene.CurrentFrameView;
        Assert.NotEqual(Vector3.Zero, view.RenderOrigin);
        Matrix4x4 shift = Matrix4x4.CreateTranslation(view.RenderOrigin);
        TemporalAssert.BitIdentical(shift * plain.ViewProjection, view.ViewProjection, "the composed view-projection");
        TemporalAssert.BitIdentical(shift * plain.View, view.View, "the composed view");
        TemporalAssert.BitIdentical(plain.ViewProjection, view.AbsoluteViewProjection, "the absolute view-projection");
        Assert.False(view.IsOrthographic);
    }

    [Fact]
    public void TheFrameIndexAdvancesOncePerBeginAndNotPerRender()
    {
        using var rig = new HeadlessSceneRig();
        rig.Frame();
        long first = rig.Scene.CurrentFrameView.FrameIndex;
        rig.Frame();
        Assert.Equal(first + 1, rig.Scene.CurrentFrameView.FrameIndex);
        rig.Render();   // a second render inside the same frame
        Assert.Equal(first + 1, rig.Scene.CurrentFrameView.FrameIndex);
        rig.Scene.Begin();   // a Begin with no render still takes an index
        rig.Frame();
        Assert.Equal(first + 3, rig.Scene.CurrentFrameView.FrameIndex);
    }

    [Fact]
    public void JitterIsZeroUntilTemporalIsForcedThenFollowsTheSequence()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();
        Assert.False(scene.TemporalActive);
        Assert.Equal(Vector2.Zero, scene.CurrentFrameView.JitterPixels);

        scene.ForceTemporalForTests = true;
        for (int i = 0; i < 9; i++)
        {
            rig.Frame();
            FrameView view = scene.CurrentFrameView;
            Assert.True(scene.TemporalActive);
            Assert.Equal(TemporalJitter.Offset(view.FrameIndex, TemporalJitter.NativePhaseCount), view.JitterPixels);
            Assert.NotEqual(view.ViewProjection, view.JitteredViewProjection);
        }

        scene.ForceTemporalForTests = false;
        rig.Frame();
        Assert.Equal(Vector2.Zero, scene.CurrentFrameView.JitterPixels);
    }

    /// <summary>A consumer camera that implements only the read-only surface, so it cannot build its view against a
    /// render origin.</summary>
    sealed class PlainCamera : IIsoCamera3D
    {
        static readonly Vector3 Target = new(1000f, 0f, 1000f);
        public Vector3 Eye => new(1006f, 9f, 1012f);
        public Vector3 Forward => Vector3.Normalize(Target - Eye);
        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(1f, 4f / 3f, 0.1f, 200f);
        public Matrix4x4 ViewProjection => View * Projection;

        public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel)
        {
            screenPixel = default;
            return false;
        }
    }
}
