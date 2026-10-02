using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using KhaozEngine.Showcase;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Showcase;

public sealed class Room3DCameraRigTests
{
    readonly MouseFrames mouse = new();

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    public void RoomDrag_LooksUpAlongGroundConstrainedBoom(MouseButton button)
    {
        FollowCameraController controller = Room3DCameraRig.Create(
            new Vector3(0f, 0.9f, 0f), 0.9f, static (_, _) => 0f, null);
        FollowCamera3D camera = controller.Camera;
        camera.BoomProbe = new FlatFloorProbe();

        controller.Update(Frame(button), 1f / 60f);
        controller.Update(Frame(button, new Vector2(0f, -200f)), 1f / 60f);

        Assert.True(controller.WantsPointerCapture);
        Assert.True(camera.Pitch < 0f);
        Assert.Equal(0.3f, camera.Eye.Y, 4);
        Assert.True(camera.Forward.Y > 0.9f, camera.Forward.ToString());
        Assert.True(Vector3.Distance(camera.Eye, camera.Pivot) < camera.Distance);
        AssertFinite(camera.View);
    }

    [Fact]
    public void RoomRig_UsesProvidedPhysicsForGroundPullIn()
    {
        using IPhysicsWorld physics = new BepuPhysicsWorld();
        physics.AddStatic(new BoxShape(new Vector3(20f, 0.5f, 20f)), Pose.At(new Vector3(0f, -0.5f, 0f)));
        physics.Step(1f / 60f);
        FollowCameraController controller = Room3DCameraRig.Create(
            new Vector3(0f, 0.9f, 0f), 0.9f, static (_, _) => 0f, physics);

        controller.Update(Frame(MouseButton.Right), 1f / 60f);
        controller.Update(Frame(MouseButton.Right, new Vector2(0f, -200f)), 1f / 60f);

        Assert.Equal(0.3f, controller.Camera.Eye.Y, 3);
        Assert.True(controller.Camera.Forward.Y > 0.8f, controller.Camera.Forward.ToString());
        Assert.True(controller.Camera.OcclusionSweepCount > 0);
        AssertFinite(controller.Camera.View);
    }

    [Theory]
    [InlineData(0.9f)]
    [InlineData(1.2f)]
    public void RoomRig_AimsOneAndAHalfMetresAboveFeetFromCapsuleCentre(float halfHeight)
    {
        var target = new Vector3(3f, halfHeight, 7f);
        FollowCameraController controller = Room3DCameraRig.Create(target, halfHeight, static (_, _) => 0f, null);

        Assert.Equal(target, controller.Camera.Target);
        Assert.Equal(new Vector3(3f, 1.5f, 7f), controller.Camera.Pivot);
        Assert.Equal(MathF.PI, controller.Camera.Yaw);
        Assert.Equal(9f, controller.Camera.Distance);
    }

    [Fact]
    public void RoomInput_ClampsPitchAndWheelZoomToMouseLookStops()
    {
        FollowCameraController controller = Room3DCameraRig.Create(
            new Vector3(0f, 0.9f, 0f), 0.9f, static (_, _) => 0f, null);

        controller.Update(Frame(MouseButton.Right), 1f / 60f);
        controller.Update(Frame(MouseButton.Right, new Vector2(0f, 1000f)), 1f / 60f);
        Assert.Equal(1.36f, controller.Camera.Pitch);

        controller.Update(Frame(MouseButton.Right, new Vector2(0f, -1000f), 1000f), 1f / 60f);
        Assert.Equal(-1.3962634f, controller.Camera.Pitch, 6);
        Assert.Equal(1.5f, controller.Camera.Distance);
        Assert.True(controller.Camera.Forward.Y > 0f);
        AssertFinite(controller.Camera.View);

        controller.Update(Frame(scroll: -1000f), 1f / 60f);
        Assert.Equal(22f, controller.Camera.Distance);
        AssertFinite(controller.Camera.View);
    }

    [Fact]
    public void Preset_InitialBoomOrbitsRaisedPivotWithoutEyeOnlyLift()
    {
        FollowCamera3D camera = FollowCamera3DPresets.CreateMouseLook();

        Assert.Equal(new Vector3(0f, 1.5f, 0f), camera.Pivot);
        Assert.Equal(12f, camera.Distance);
        Assert.Equal(0.75f, camera.Pitch);
        var expectedEye = new Vector3(0f, 9.679665f, 8.780266f);
        Assert.True(Vector3.Distance(expectedEye, camera.Eye) < 0.00002f, camera.Eye.ToString());
        Assert.True(camera.Forward.Y < 0f);
        Assert.False(camera.EnableTargetDamping);
    }

    [Fact]
    public void Preset_ControllerEasesBoomOutAfterInstantPullIn()
    {
        FollowCamera3D camera = FollowCamera3DPresets.CreateMouseLook();
        camera.Pitch = 0f;
        var probe = new ReachProbe { Limit = 3f };
        camera.BoomProbe = probe;
        Assert.Equal(2.95f, camera.Eye.Z, 4);

        probe.Limit = null;
        var controller = new FollowCameraController(camera);
        controller.Update(Frame(), 0.25f);
        camera.BeginFrame();

        Assert.InRange(camera.Eye.Z, 8.66f, 8.68f);
        Assert.Equal(1.5f, camera.Eye.Y);
    }

    [Fact]
    public void RoomRig_KeepsOptInTargetDamping()
    {
        FollowCameraController controller = Room3DCameraRig.Create(
            new Vector3(0f, 0.9f, 0f), 0.9f, static (_, _) => 0f, null);
        controller.Update(Frame(), 0.1f);
        controller.Camera.Target = new Vector3(10f, 0.9f, 0f);

        controller.Update(Frame(), 0.1f);

        Assert.InRange(controller.Camera.EffectiveTarget.X, 6.32f, 6.33f);
        Assert.Equal(1.5f, controller.Camera.Pivot.Y);
    }

    [Fact]
    public void LegacyCamera_StillLooksDownAndUsesOriginalZoomStops()
    {
        var camera = new FollowCamera3D();
        var controller = new FollowCameraController(camera);
        Assert.True(Vector3.Distance(new Vector3(0f, 5f, 6.928203f), camera.Eye) < 0.00001f);

        controller.Update(Frame(MouseButton.Right, new Vector2(0f, -1000f), 1000f), 1f / 60f);
        Assert.Equal(MathF.PI / 30f, camera.Pitch);
        Assert.Equal(2f, camera.Distance);
        Assert.True(camera.Forward.Y < 0f);

        controller.Update(Frame(scroll: -1000f), 1f / 60f);
        Assert.Equal(30f, camera.Distance);
        Assert.Equal(Vector3.Zero, camera.Pivot);
    }

    [Fact]
    public void Preset_CamerasDoNotShareMutableConfiguration()
    {
        FollowCamera3D first = FollowCamera3DPresets.CreateMouseLook();
        FollowCamera3D second = FollowCamera3DPresets.CreateMouseLook();
        Vector3 before = second.Eye;

        first.Target = new Vector3(7f, 5f, 3f);
        first.Pitch = -0.5f;
        first.Distance = 2f;

        Assert.NotSame(first, second);
        Assert.Equal(before, second.Eye);
    }

    InputState Frame(MouseButton? down = null, Vector2 delta = default, float scroll = 0f)
    {
        var held = new HashSet<MouseButton>();
        if (down is MouseButton button) held.Add(button);
        var (pressed, released) = mouse.Advance(held);
        return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(), held, pressed,
            Vector2.Zero, delta, scroll, 800, 600, mouseReleased: released);
    }

    static void AssertFinite(Matrix4x4 matrix)
    {
        Assert.All(new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 }, value => Assert.True(float.IsFinite(value)));
    }

    sealed class FlatFloorProbe : ICameraBoomProbe
    {
        public float Reach(Vector3 origin, Vector3 direction, float length, float radius) => direction.Y < 0f
            ? MathF.Min(length, MathF.Max(0f, (origin.Y - radius) / -direction.Y)) : length;
    }

    sealed class ReachProbe : ICameraBoomProbe
    {
        public float? Limit;
        public float Reach(Vector3 origin, Vector3 direction, float length, float radius) =>
            Limit is float limit ? MathF.Min(length, limit) : length;
    }
}
