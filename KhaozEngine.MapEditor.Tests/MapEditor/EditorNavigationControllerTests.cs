using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapEditor;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    public sealed class EditorNavigationControllerTests
    {
        readonly MouseFrames _mouse = new();

        [Fact]
        public void OrbitHoldsPressPivotForWholeGesture()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);
            var pivotAtPress = new Vector3(3f, 0f, 7f);

            navigation.Update(Frame(MouseButton.Middle, down: true), viewportEligible: true, pivotAtPress, 0.016f);
            navigation.Update(Frame(MouseButton.Middle, down: true, delta: new Vector2(30f, -10f), scroll: 1f),
                viewportEligible: true, terrainHit: new Vector3(90f, 0f, 90f), 0.016f);

            Assert.True(navigation.IsNavigating);
            Assert.Equal(pivotAtPress, navigation.Pivot);
        }

        [Fact]
        public void OrbitTinyDeltaRotatesExistingEyeOffsetWithoutJumping()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);
            var pivot = new Vector3(3f, 0f, 7f);
            Vector3 eyeAtPress = camera.Position;
            float distanceAtPress = Vector3.Distance(eyeAtPress, pivot);
            navigation.Update(Frame(MouseButton.Middle, down: true), true, pivot, 0.016f);

            navigation.Update(Frame(MouseButton.Middle, down: true, delta: new Vector2(0.001f, -0.001f)),
                true, new Vector3(90f, 0f, 90f), 0.016f);

            Assert.True(Vector3.Distance(eyeAtPress, camera.Position) < 0.001f);
            AssertNear(distanceAtPress, Vector3.Distance(camera.Position, pivot), 0.0001f);
        }

        [Fact]
        public void ShiftMiddleCapturesPanModeAtPress()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);
            Vector3 pivotAtPress = Vector3.Zero;
            float yawBefore = camera.Yaw;
            float pitchBefore = camera.Pitch;

            navigation.Update(Frame(MouseButton.Middle, down: true, shift: true), true, pivotAtPress, 0.016f);
            navigation.Update(Frame(MouseButton.Middle, down: true, delta: new Vector2(20f, 8f)),
                true, new Vector3(100f), 0.016f);

            Assert.Equal(yawBefore, camera.Yaw);
            Assert.Equal(pitchBefore, camera.Pitch);
            Assert.NotEqual(pivotAtPress, navigation.Pivot);
            AssertVectorNear(camera.Position - new Vector3(0f, 12f, -20f), navigation.Pivot!.Value - pivotAtPress);
        }

        [Fact]
        public void WheelDollyClampsDistanceAndDoesNotChangeFlySpeed()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera) { FlySpeed = 31f };
            var pivot = Vector3.Zero;
            navigation.Update(Frame(MouseButton.Middle, down: true), true, pivot, 0.016f);
            navigation.Update(Frame(MouseButton.Middle, down: false), true, pivot, 0.016f);

            navigation.Update(Frame(scroll: 1000f), true, pivot, 0.016f);
            AssertNear(0.5f, Vector3.Distance(camera.Position, pivot));
            Assert.Equal(31f, navigation.FlySpeed);

            navigation.Update(Frame(scroll: -1000f), true, pivot, 0.016f);
            AssertNear(100000f, Vector3.Distance(camera.Position, pivot), 0.1f);
            Assert.Equal(31f, navigation.FlySpeed);
        }

        [Fact]
        public void MissUsesForwardFallbackThenRetainsLastPivot()
        {
            var camera = new FlyCamera3D { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
            var navigation = new EditorNavigationController(camera);

            navigation.Update(Frame(MouseButton.Middle, down: true), true, terrainHit: null, 0.016f);
            Assert.Equal(new Vector3(0f, 0f, 25f), navigation.Pivot);
            navigation.Update(Frame(MouseButton.Middle, down: false), true, null, 0.016f);

            camera.Position = new Vector3(50f, 50f, 50f);
            navigation.Update(Frame(MouseButton.Middle, down: true), true, terrainHit: null, 0.016f);
            Assert.Equal(new Vector3(0f, 0f, 25f), navigation.Pivot);
        }

        [Fact]
        public void CancelledHeldButtonRequiresReleaseAndFreshPress()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);
            navigation.Update(Frame(MouseButton.Middle, down: true), true, Vector3.Zero, 0.016f);
            navigation.Cancel();
            Vector3 cancelledAt = camera.Position;

            navigation.Update(Frame(MouseButton.Middle, down: true, delta: new Vector2(40f, 20f)),
                true, Vector3.Zero, 0.016f);
            Assert.False(navigation.IsNavigating);
            Assert.Equal(cancelledAt, camera.Position);

            navigation.Update(Frame(MouseButton.Middle, down: false), true, Vector3.Zero, 0.016f);
            navigation.Update(Frame(MouseButton.Middle, down: true), true, Vector3.Zero, 0.016f);
            Assert.True(navigation.IsNavigating);
        }

        [Fact]
        public void FocusLossCancelsNavigation()
        {
            var navigation = new EditorNavigationController(Camera());
            navigation.Update(Frame(MouseButton.Middle, down: true), true, Vector3.Zero, 0.016f);

            navigation.Update(Frame(MouseButton.Middle, down: true, focused: false), true, Vector3.Zero, 0.016f);

            Assert.False(navigation.IsNavigating);
        }

        [Fact]
        public void RightMouseFlyRequiresAcquisitionAndUsesExplicitSpeed()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera) { FlySpeed = 10f };
            Vector3 before = camera.Position;

            navigation.Update(Frame(MouseButton.Right, down: true, keys: new[] { Key.W }, scroll: 2f),
                viewportEligible: false, terrainHit: null, dt: 1f);
            Assert.Equal(before, camera.Position);
            Assert.False(navigation.IsNavigating);

            navigation.Update(Frame(MouseButton.Right, down: false), true, null, 0.016f);
            navigation.Update(Frame(MouseButton.Right, down: true, keys: new[] { Key.W }),
                viewportEligible: true, terrainHit: Vector3.Zero, dt: 1f);

            Assert.True(navigation.IsNavigating);
            AssertNear(10f, Vector3.Distance(before, camera.Position));
            navigation.Update(Frame(MouseButton.Right, down: true, scroll: 2f), true, Vector3.Zero, 0.016f);
            Assert.Equal(10f, navigation.FlySpeed);
        }

        [Fact]
        public void CommandModifierSuppressesCapturedFlyMotionForFrame()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);
            navigation.Update(Frame(MouseButton.Right, down: true), true, Vector3.Zero, 0.016f);
            Vector3 before = camera.Position;
            float yawBefore = camera.Yaw;

            navigation.Update(Frame(MouseButton.Right, down: true, delta: new Vector2(50f, 0f),
                keys: new[] { Key.LeftSuper, Key.D }), true, Vector3.Zero, 1f);

            Assert.Equal(before, camera.Position);
            Assert.Equal(yawBefore, camera.Yaw);
            Assert.True(navigation.IsNavigating);
        }

        [Fact]
        public void NonFiniteFrameDataCannotMakeCameraNonFinite()
        {
            var camera = Camera();
            var navigation = new EditorNavigationController(camera);

            navigation.Update(Frame(MouseButton.Middle, down: true, delta: new Vector2(float.NaN, float.PositiveInfinity),
                scroll: float.NegativeInfinity), true, new Vector3(float.NaN, 1f, 2f), float.NaN);

            Assert.True(IsFinite(camera.Position));
            Assert.True(float.IsFinite(camera.Yaw));
            Assert.True(float.IsFinite(camera.Pitch));
            Assert.True(navigation.Pivot is null || IsFinite(navigation.Pivot.Value));
        }

        static FlyCamera3D Camera() => new()
        {
            Position = new Vector3(0f, 12f, -20f),
            Yaw = 0f,
            Pitch = -0.45f,
        };

        InputState Frame(MouseButton? button = null, bool down = false, Vector2 delta = default,
            float scroll = 0f, bool shift = false, bool focused = true, IEnumerable<Key>? keys = null)
        {
            var mouseDown = new HashSet<MouseButton>();
            if (button is MouseButton held && down) mouseDown.Add(held);
            var (pressed, released) = _mouse.Advance(mouseDown);
            var keysDown = keys is null ? new HashSet<Key>() : new HashSet<Key>(keys);
            if (shift) keysDown.Add(Key.LeftShift);
            return new InputState(keysDown, new HashSet<Key>(), new HashSet<Key>(), mouseDown, pressed,
                new Vector2(480f, 270f), delta, scroll, 960, 540, windowFocused: focused,
                mouseReleased: released);
        }

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static void AssertNear(float expected, float actual, float epsilon = 0.001f) =>
            Assert.True(MathF.Abs(expected - actual) <= epsilon, $"expected {expected} but got {actual}");

        static void AssertVectorNear(Vector3 expected, Vector3 actual, float epsilon = 0.001f)
        {
            AssertNear(expected.X, actual.X, epsilon);
            AssertNear(expected.Y, actual.Y, epsilon);
            AssertNear(expected.Z, actual.Z, epsilon);
        }
    }
}
