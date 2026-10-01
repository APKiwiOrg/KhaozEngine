using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The follow camera's optional orbit and look gestures. With neither set the controller is today's
    /// button-held orbit, pinned exactly. With either set, each gesture's drag orbits, a look drag also turns the
    /// body, any drag asks for pointer capture, and a quick click is a tap read from the gesture after the update.
    /// </summary>
    public class FollowCameraGestureTests
    {
        const float Dt = 1f / 60f;

        static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
        static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();

        Vector2 _cursor = new(400f, 300f);

        // One frame. The cursor advances by the delta so a tap's press origin and the travel agree.
        InputState Frame(Vector2 delta = default, float scroll = 0f, bool focused = true, MouseButton[]? down = null)
        {
            _cursor += delta;
            return new InputState(
                NoKeys, NoKeys, NoKeys, new HashSet<MouseButton>(down ?? []), NoButtons,
                _cursor, delta, scroll, 800, 600, windowFocused: focused);
        }

        static FollowCamera3D NewCamera()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            cam.Distance = 10f;
            return cam;
        }

        static FollowCameraController WowStyle(FollowCamera3D cam) => new(cam)
        {
            OrbitGesture = new PointerGesture(MouseButton.Left),
            LookGesture = new PointerGesture(MouseButton.Right),
        };

        static void AssertBitwise(float expected, float actual) =>
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));

        [Theory]
        [InlineData(MouseButton.Middle)]
        [InlineData(MouseButton.Right)]
        public void NoGesturesKeepsTodaysOrbitExactly(MouseButton orbitButton)
        {
            // The reference camera takes today's Update body by hand: orbit from the first frame of the press while
            // the button is held, scroll zoom by the step. Every value must match to the bit.
            FollowCamera3D cam = NewCamera();
            FollowCamera3D reference = NewCamera();
            var ctl = new FollowCameraController(cam) { OrbitButton = orbitButton, InvertY = true };
            MouseButton other = orbitButton == MouseButton.Middle ? MouseButton.Right : MouseButton.Middle;

            var frames = new (Vector2 Delta, float Scroll, MouseButton[] Down)[]
            {
                (new Vector2(3f, -2f), 0f, new[] { orbitButton }),          // the press frame already orbits
                (new Vector2(10f, 4f), 0f, new[] { orbitButton }),
                (new Vector2(0.37f, 1.9f), 0f, new[] { orbitButton, MouseButton.Left }),
                (new Vector2(25f, -7f), 0f, new[] { other }),               // the wrong button never orbits
                (new Vector2(-6f, 5f), 0f, new[] { MouseButton.Left }),
                (new Vector2(2f, 2f), 1.5f, new[] { orbitButton }),
                (Vector2.Zero, -0.75f, Array.Empty<MouseButton>()),
            };

            foreach (var (delta, scroll, down) in frames)
            {
                ctl.Update(Frame(delta, scroll, down: down), Dt);

                if (Array.IndexOf(down, orbitButton) >= 0)
                {
                    float yaw = delta.X * ctl.OrbitYawSpeed;
                    float pitch = delta.Y * ctl.OrbitPitchSpeed;
                    if (ctl.InvertX) yaw = -yaw;
                    if (ctl.InvertY) pitch = -pitch;
                    reference.Yaw -= yaw;
                    reference.Pitch += pitch;
                }
                if (scroll != 0f) reference.Distance *= MathF.Pow(ctl.ZoomStep, -scroll);

                AssertBitwise(reference.Yaw, cam.Yaw);
                AssertBitwise(reference.Pitch, cam.Pitch);
                AssertBitwise(reference.Distance, cam.Distance);
                Assert.False(ctl.TurnBodyActive);
                Assert.False(ctl.WantsPointerCapture);
            }
        }

        [Fact]
        public void AnOrbitDragPastTheThresholdOrbitsWithoutTurningTheBody()
        {
            FollowCamera3D cam = NewCamera();
            var ctl = new FollowCameraController(cam) { OrbitGesture = new PointerGesture(MouseButton.Left) };

            // A gesture is set, so OrbitButton (Right by default) no longer orbits.
            ctl.Update(Frame(new Vector2(20f, 20f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);

            ctl.Update(Frame(down: [MouseButton.Left]), Dt);                        // press: undecided
            ctl.Update(Frame(new Vector2(3f, 0f), down: [MouseButton.Left]), Dt);   // 3 px, under the threshold
            Assert.Equal(0f, cam.Yaw);
            Assert.False(ctl.WantsPointerCapture);

            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Left]), Dt);   // 5 px, crossed: catch up 5
            Assert.Equal(-5f * ctl.OrbitYawSpeed, cam.Yaw, 5);
            Assert.True(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);

            ctl.Update(Frame(new Vector2(10f, 4f), down: [MouseButton.Left]), Dt);
            Assert.Equal(-15f * ctl.OrbitYawSpeed, cam.Yaw, 5);
            Assert.Equal(0.5f + 4f * ctl.OrbitPitchSpeed, cam.Pitch, 5);
            Assert.False(ctl.TurnBodyActive);

            ctl.Update(Frame(), Dt);                                               // release ends the drag
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.OrbitGesture!.TapThisFrame);
        }

        [Fact]
        public void ALookDragOrbitsAndTurnsTheBody()
        {
            FollowCamera3D cam = NewCamera();
            var ctl = new FollowCameraController(cam) { InvertX = true, InvertY = true };
            ctl.OrbitGesture = new PointerGesture(MouseButton.Left);
            ctl.LookGesture = new PointerGesture(MouseButton.Right);

            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            Assert.False(ctl.TurnBodyActive);                                      // pending is not yet a look

            ctl.Update(Frame(new Vector2(6f, 2f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.TurnBodyActive);
            Assert.True(ctl.WantsPointerCapture);
            Assert.Equal(6f * ctl.OrbitYawSpeed, cam.Yaw, 5);                      // inverted axes still apply
            Assert.Equal(0.5f - 2f * ctl.OrbitPitchSpeed, cam.Pitch, 5);

            ctl.Update(Frame(Vector2.Zero, down: [MouseButton.Right]), Dt);          // held still: still a look
            Assert.True(ctl.TurnBodyActive);

            ctl.Update(Frame(), Dt);
            Assert.False(ctl.TurnBodyActive);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.LookGesture.TapThisFrame);
        }

        [Fact]
        public void WantsPointerCaptureFollowsAnyDrag()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);
            Assert.False(ctl.WantsPointerCapture);

            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            Assert.False(ctl.WantsPointerCapture);                                 // a press is not a drag
            ctl.Update(Frame(new Vector2(8f, 0f), down: [MouseButton.Left]), Dt);
            Assert.True(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);

            ctl.Update(Frame(Vector2.Zero, down: new[] { MouseButton.Left, MouseButton.Right }), Dt);
            ctl.Update(Frame(new Vector2(8f, 0f), down: new[] { MouseButton.Left, MouseButton.Right }), Dt);
            Assert.True(ctl.TurnBodyActive);

            // Both dragging: the frame's orbit is the sum of the two drag deltas.
            float before = cam.Yaw;
            ctl.Update(Frame(new Vector2(3f, 0f), down: new[] { MouseButton.Left, MouseButton.Right }), Dt);
            Assert.Equal(before - 6f * ctl.OrbitYawSpeed, cam.Yaw, 5);

            ctl.Update(Frame(down: [MouseButton.Right]), Dt);                       // left up, look still drags
            Assert.True(ctl.WantsPointerCapture);
            Assert.True(ctl.TurnBodyActive);

            ctl.Update(Frame(), Dt);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
        }

        [Fact]
        public void AQuickClickIsATapAndDoesNotOrbit()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            _cursor = new Vector2(120f, 80f);
            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(1f, 1f), down: [MouseButton.Left]), Dt);   // tremor under the threshold
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.OrbitGesture!.TapThisFrame);
            Assert.Equal(new Vector2(120f, 80f), ctl.OrbitGesture.TapPosition);
            Assert.False(ctl.LookGesture!.TapThisFrame);

            ctl.Update(Frame(), Dt);
            Assert.False(ctl.OrbitGesture.TapThisFrame);                           // one frame only

            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.LookGesture.TapThisFrame);
            Assert.False(ctl.OrbitGesture.TapThisFrame);

            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
        }

        [Fact]
        public void LosingFocusDuringAHoldIsNotATap()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            // A pending press, then focus goes. The accumulator releases held buttons on unfocus, so the button
            // reads up on that frame, which must not look like a tap.
            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(focused: false), Dt);
            Assert.False(ctl.LookGesture!.TapThisFrame);
            ctl.Update(Frame(), Dt);
            Assert.False(ctl.LookGesture.TapThisFrame);

            // A look drag in progress, then focus goes with the button still read as held: the drag stops and
            // capture is no longer wanted.
            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(8f, 0f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.WantsPointerCapture);
            float yaw = cam.Yaw;

            ctl.Update(Frame(new Vector2(30f, 0f), focused: false, down: [MouseButton.Right]), Dt);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
            Assert.Equal(yaw, cam.Yaw);

            // Back in focus with the same hold: the rest of that press is inert, and its release is no tap.
            ctl.Update(Frame(new Vector2(30f, 0f), down: [MouseButton.Right]), Dt);
            Assert.False(ctl.WantsPointerCapture);
            Assert.Equal(yaw, cam.Yaw);
            ctl.Update(Frame(), Dt);
            Assert.False(ctl.LookGesture.TapThisFrame);
        }

        [Fact]
        public void UiBlockedPressesNeverOrbit()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            ctl.UiBlocked = true;
            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(40f, 12f), down: [MouseButton.Left]), Dt);
            Assert.False(ctl.WantsPointerCapture);

            ctl.UiBlocked = false;                                                 // the pointer leaves the UI mid-hold
            ctl.Update(Frame(new Vector2(40f, 12f), down: [MouseButton.Left]), Dt);
            Assert.False(ctl.WantsPointerCapture);
            ctl.Update(Frame(), Dt);
            Assert.False(ctl.OrbitGesture!.TapThisFrame);

            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);

            // A fresh press off the UI drags again.
            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(8f, 0f), down: [MouseButton.Left]), Dt);
            Assert.Equal(-8f * ctl.OrbitYawSpeed, cam.Yaw, 5);
        }

        [Fact]
        public void ScrollStillZoomsWithGesturesSet()
        {
            FollowCamera3D cam = NewCamera();
            cam.Target = Vector3.Zero;
            cam.EnableTargetDamping = true;
            cam.TargetDampingRate = 10f;
            FollowCameraController ctl = WowStyle(cam);

            ctl.Update(Frame(scroll: 1f), Dt);
            Assert.Equal(10f * MathF.Pow(ctl.ZoomStep, -1f), cam.Distance, 4);
            float after = cam.Distance;
            ctl.Update(Frame(scroll: -1f), Dt);
            Assert.True(cam.Distance > after, $"scroll down should increase distance: {cam.Distance}");

            // Target damping still advances with gestures set.
            cam.Target = new Vector3(10f, 0f, 0f);
            for (int i = 0; i < 5; i++) ctl.Update(Frame(), Dt);
            float x = cam.EffectiveTarget.X;
            Assert.True(x > 0f && x < 10f, $"controller should be easing the target, got {x}");
        }
    }
}
