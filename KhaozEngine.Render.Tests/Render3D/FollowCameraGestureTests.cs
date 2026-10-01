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
    /// body, any drag asks for pointer capture, and a quick click is a tap read from the controller after the update.
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

            // Both held: each frame's movement turns the camera once, however many buttons drag.
            float before = cam.Yaw;
            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Left, MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(8f, 0f), down: [MouseButton.Left, MouseButton.Right]), Dt);
            Assert.True(ctl.TurnBodyActive);
            ctl.Update(Frame(new Vector2(3f, 0f), down: [MouseButton.Left, MouseButton.Right]), Dt);
            Assert.Equal(before - 13f * ctl.OrbitYawSpeed, cam.Yaw, 5);

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
            Assert.True(ctl.OrbitTap);
            Assert.True(ctl.OrbitGesture!.TapThisFrame);
            Assert.Equal(new Vector2(120f, 80f), ctl.OrbitGesture.TapPosition);
            Assert.False(ctl.LookTap);

            ctl.Update(Frame(), Dt);
            Assert.False(ctl.OrbitTap);                                            // one frame only

            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.LookTap);
            Assert.False(ctl.OrbitTap);

            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
        }

        [Fact]
        public void ALeftTapDuringALookDragIsNotATap()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            // Right crosses into a look drag.
            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(6f, 0f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.TurnBodyActive);

            // Left goes down and up under its threshold while the look keeps turning. The gesture alone calls that a
            // tap, but the camera turned during that press, so it is never a select.
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.OrbitGesture!.TapThisFrame);
            Assert.False(ctl.OrbitTap);
            Assert.True(ctl.TurnBodyActive);

            // The look ends as a drag, so it is no tap either.
            ctl.Update(Frame(), Dt);
            Assert.False(ctl.LookTap);
            Assert.False(ctl.OrbitTap);

            // The latch belongs to that press only. A fresh left click with nothing dragging taps again.
            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.OrbitTap);
        }

        [Fact]
        public void ALeftPressBeganBeforeTheLookCrossedIsNotATapOnceTheCameraTurns()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            // Right goes down, then left joins while right is still undecided.
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);
            Assert.False(ctl.TurnBodyActive);

            // Right crosses with 5 px of travel and the camera turns. Left has 4 px, still undecided.
            ctl.Update(Frame(new Vector2(3f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);
            Assert.True(ctl.TurnBodyActive);
            Assert.Equal(PointerGesturePhase.Pending, ctl.OrbitGesture!.Phase);
            Assert.Equal(-5f * ctl.OrbitYawSpeed, cam.Yaw, 5);

            // Left comes up under its threshold. The gesture alone calls it a tap, but the camera turned during
            // that press, so it is no select.
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.OrbitGesture.TapThisFrame);
            Assert.False(ctl.OrbitTap);
            Assert.False(ctl.LookTap);

            // Right ends as a drag, so neither reports a tap.
            ctl.Update(Frame(), Dt);
            Assert.False(ctl.LookTap);
            Assert.False(ctl.OrbitTap);

            // With nothing turning, plain clicks on either button tap again.
            ctl.Update(Frame(down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.OrbitTap);
            ctl.Update(Frame(down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(), Dt);
            Assert.True(ctl.LookTap);
            Assert.False(ctl.OrbitTap);
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

            // A look drag in progress, then focus goes. The accumulator's unfocus snapshot is unfocused with the
            // button released, so the drag ends there: no orbit, no capture, no tap.
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(8f, 0f), down: [MouseButton.Right]), Dt);
            Assert.True(ctl.WantsPointerCapture);
            float yaw = cam.Yaw;

            ctl.Update(Frame(new Vector2(30f, 0f), focused: false), Dt);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
            Assert.False(ctl.LookGesture.TapThisFrame);
            Assert.Equal(yaw, cam.Yaw);

            // Back in focus, the button stays up until a new press.
            ctl.Update(Frame(new Vector2(30f, 0f)), Dt);
            Assert.False(ctl.WantsPointerCapture);
            Assert.False(ctl.LookGesture.TapThisFrame);
            Assert.Equal(yaw, cam.Yaw);
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

        [Fact]
        public void BoomStillRecoversWithGesturesSet()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero,
                Yaw = 0f,
                HeightOffset = 0f,
                MinPitch = 0f,
                BoomProbe = probe,
                BoomRecoveryRate = 4f,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            FollowCameraController ctl = WowStyle(cam);
            Assert.Equal(3.95f, Vector3.Distance(cam.Eye, cam.Pivot), 4);   // pulled in

            probe.ReachAt = null;
            ctl.Update(Frame(), 0.1f);
            float length = Vector3.Distance(cam.Eye, cam.Pivot);

            Assert.True(length > 3.95f && length < 10f, $"controller should be easing the boom out, got {length}");
        }

        [Theory]
        [InlineData(MouseButton.Left)]
        [InlineData(MouseButton.Right)]
        public void ASingleGesturesFirstCrossingReplaysItsPendingTravel(MouseButton button)
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            ctl.Update(Frame(new Vector2(1f, 0f), down: [button]), Dt);
            ctl.Update(Frame(new Vector2(2f, 0f), down: [button]), Dt);   // 3 px, still undecided
            Assert.Equal(0f, cam.Yaw);

            ctl.Update(Frame(new Vector2(3f, 0f), down: [button]), Dt);   // 6 px, crossed: the whole travel at once
            Assert.Equal(-6f * ctl.OrbitYawSpeed, cam.Yaw, 5);

            ctl.Update(Frame(new Vector2(1f, 0f), down: [button]), Dt);
            Assert.Equal(-7f * ctl.OrbitYawSpeed, cam.Yaw, 5);
        }

        [Fact]
        public void ASecondButtonJoiningADragDoesNotReplayMovementAlreadyApplied()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Left]), Dt);
            ctl.Update(Frame(new Vector2(5f, 0f), down: [MouseButton.Left]), Dt);   // left drags
            float start = cam.Yaw;

            // Right joins with 3 px, then crosses with 2 more. Left already turned the camera by the 3, so the
            // crossing applies only the 2, not Right's 5 px replay on top.
            ctl.Update(Frame(new Vector2(3f, 0f), down: [MouseButton.Left, MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Left, MouseButton.Right]), Dt);
            Assert.True(ctl.TurnBodyActive);
            Assert.Equal(start - 5f * ctl.OrbitYawSpeed, cam.Yaw, 5);
        }

        [Fact]
        public void AHandoffFromLookToOrbitDoesNotReplayWhatTheLookApplied()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(5f, 0f), down: [MouseButton.Right]), Dt);  // look drags: 6
            ctl.Update(Frame(new Vector2(2f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);   // left pending: 8
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);   // 9
            ctl.Update(Frame(Vector2.Zero, down: [MouseButton.Left]), Dt);          // right up, left still pending
            Assert.False(ctl.WantsPointerCapture);

            // Left crosses with 7 px of travel, but the look already applied 3 of them, so only this frame's 4 turns.
            ctl.Update(Frame(new Vector2(4f, 0f), down: [MouseButton.Left]), Dt);
            Assert.True(ctl.WantsPointerCapture);
            Assert.False(ctl.TurnBodyActive);
            Assert.Equal(-13f * ctl.OrbitYawSpeed, cam.Yaw, 5);
        }

        [Fact]
        public void TwoButtonsCrossingTogetherReplayTheLookOnce()
        {
            FollowCamera3D cam = NewCamera();
            FollowCameraController ctl = WowStyle(cam);

            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right]), Dt);
            ctl.Update(Frame(new Vector2(1f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);

            // Both cross on this frame: right with 6 px of travel, left with 5. Nothing has orbited yet, so the
            // look's replay applies, once.
            ctl.Update(Frame(new Vector2(4f, 0f), down: [MouseButton.Right, MouseButton.Left]), Dt);
            Assert.True(ctl.TurnBodyActive);
            Assert.True(ctl.OrbitGesture!.Phase == PointerGesturePhase.Dragging);
            Assert.Equal(-6f * ctl.OrbitYawSpeed, cam.Yaw, 5);
        }
    }
}
