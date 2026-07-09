using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless tests for the editor fly camera: movement basis, sprint, look gating and
    /// clamping, wheel speed scaling, and projection round-trips.</summary>
    public class FlyCameraTests
    {
        static InputState Frame(
            IEnumerable<Key>? keysDown = null,
            IEnumerable<MouseButton>? mouseDown = null,
            Vector2 mouseDelta = default,
            float scroll = 0f)
        {
            var down = new HashSet<Key>(keysDown ?? System.Array.Empty<Key>());
            var mDown = new HashSet<MouseButton>(mouseDown ?? System.Array.Empty<MouseButton>());
            return new InputState(
                down, new HashSet<Key>(), new HashSet<Key>(),
                mDown, new HashSet<MouseButton>(), new Vector2(480, 270), mouseDelta, scroll, 960, 540);
        }

        [Fact]
        public void W_MovesAlongForward()
        {
            var cam = new FlyCamera3D { Position = Vector3.Zero };
            var ctl = new FlyCameraController(cam) { MoveSpeed = 10f };
            Vector3 fwd = cam.Forward;
            ctl.Update(Frame(keysDown: new[] { Key.W }), 0.5f);
            Assert.Equal(fwd * 5f, cam.Position, Vec3Comparer);
        }

        [Fact]
        public void S_MovesBackward_AD_Strafe_EQ_Vertical()
        {
            var cam = new FlyCamera3D { Position = Vector3.Zero };
            var ctl = new FlyCameraController(cam) { MoveSpeed = 10f };
            ctl.Update(Frame(keysDown: new[] { Key.S }), 0.1f);
            Vector3 afterS = cam.Position;
            Assert.True(Vector3.Dot(afterS, cam.Forward) < 0f);

            cam.Position = Vector3.Zero;
            ctl.Update(Frame(keysDown: new[] { Key.E }), 0.1f);
            Assert.Equal(1f, cam.Position.Y, 4);
            ctl.Update(Frame(keysDown: new[] { Key.Q }), 0.1f);
            Assert.Equal(0f, cam.Position.Y, 4);

            cam.Position = Vector3.Zero;
            ctl.Update(Frame(keysDown: new[] { Key.D }), 0.1f);
            Assert.Equal(0f, cam.Position.Y, 4);           // strafing stays horizontal
            Assert.True(cam.Position.Length() > 0.9f);
        }

        [Fact]
        public void Sprint_MultipliesSpeed()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam) { MoveSpeed = 10f, SprintMultiplier = 3f };
            Vector3 start = cam.Position;
            ctl.Update(Frame(keysDown: new[] { Key.W, Key.LeftShift }), 0.1f);
            Assert.Equal(3f, (cam.Position - start).Length(), 3);
        }

        [Fact]
        public void Look_OnlyWhileLookButtonHeld()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam);
            float yaw0 = cam.Yaw;
            ctl.Update(Frame(mouseDelta: new Vector2(50f, 0f)), 0.016f);
            Assert.Equal(yaw0, cam.Yaw);
            ctl.Update(Frame(mouseDown: new[] { MouseButton.Right }, mouseDelta: new Vector2(50f, 0f)), 0.016f);
            Assert.NotEqual(yaw0, cam.Yaw);
        }

        [Fact]
        public void Pitch_IsClamped()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam);
            for (int i = 0; i < 100; i++)
                ctl.Update(Frame(mouseDown: new[] { MouseButton.Right }, mouseDelta: new Vector2(0f, -500f)), 0.016f);
            Assert.True(System.MathF.Abs(cam.Pitch) < System.MathF.PI / 2f);
            Assert.True(float.IsFinite(cam.Forward.Y));
        }

        [Fact]
        public void DragRight_LooksTowardStrafeRight()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam);
            Vector3 right = Vector3.Normalize(Vector3.Cross(cam.Forward, Vector3.UnitY));
            ctl.Update(Frame(mouseDown: new[] { MouseButton.Right }, mouseDelta: new Vector2(50f, 0f)), 0.016f);
            Assert.True(Vector3.Dot(cam.Forward, right) > 0f, "drag right should rotate the view toward strafe right");
        }

        [Fact]
        public void InvertX_FlipsYawDirection()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam) { InvertX = true };
            Vector3 right = Vector3.Normalize(Vector3.Cross(cam.Forward, Vector3.UnitY));
            ctl.Update(Frame(mouseDown: new[] { MouseButton.Right }, mouseDelta: new Vector2(50f, 0f)), 0.016f);
            Assert.True(Vector3.Dot(cam.Forward, right) < 0f, "inverted drag right should rotate the view toward strafe left");
        }

        [Fact]
        public void Wheel_ScalesMoveSpeed_Clamped()
        {
            var cam = new FlyCamera3D();
            var ctl = new FlyCameraController(cam) { MoveSpeed = 10f, SpeedWheelStep = 2f, MaxMoveSpeed = 30f };
            ctl.Update(Frame(scroll: 1f), 0.016f);
            Assert.Equal(20f, ctl.MoveSpeed, 3);
            ctl.Update(Frame(scroll: 1f), 0.016f);
            Assert.Equal(30f, ctl.MoveSpeed, 3);           // clamped
            ctl.Update(Frame(scroll: -10f), 0.016f);
            Assert.True(ctl.MoveSpeed >= ctl.MinMoveSpeed);
        }

        [Fact]
        public void WorldToScreen_ScreenToRay_RoundTrip()
        {
            var cam = new FlyCamera3D { Position = new Vector3(3f, 8f, -6f), Yaw = 0.7f, Pitch = -0.3f, AspectRatio = 960f / 540f };
            Vector3 world = cam.Position + cam.Forward * 12f + new Vector3(0.5f, -0.3f, 0.2f);
            Assert.True(cam.WorldToScreen(world, 960, 540, out Vector2 px));
            Ray ray = cam.ScreenToRay(px, 960, 540);
            // The world point must lie on the ray: distance from point to line near zero.
            Vector3 d = Vector3.Normalize(ray.Direction);
            Vector3 toPoint = world - ray.Origin;
            float along = Vector3.Dot(toPoint, d);
            float offLine = (toPoint - d * along).Length();
            Assert.True(offLine < 0.001f, $"off-line distance {offLine}");
        }

        static readonly IEqualityComparer<Vector3> Vec3Comparer = new ApproxVec3();
        sealed class ApproxVec3 : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() < 1e-3f;
            public int GetHashCode(Vector3 v) => 0;
        }
    }
}
