using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The canonical facing helper: the WASD axis and camera-relative intended direction match the movement basis
    // (so facing and travel never diverge), and the bounded-rate turn converges, holds, takes the shortest way, and
    // holds facing when no key is pressed. The "stable under a lateral collision velocity" property is proven at the
    // integration level in CharacterAvatarTests (facing tracks intent while the capsule is deflected along a wall);
    // here it is structural - the helper reads only input + camera, never a velocity.
    public class CharacterFacingTests
    {
        static InputState Keys(params Key[] down) => new(
            new HashSet<Key>(down), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 800, 600);

        [Fact]
        public void MoveAxis_MapsWasd()
        {
            Assert.Equal(new Vector2(0f, 1f), CharacterFacing.MoveAxis(Keys(Key.W)));
            Assert.Equal(new Vector2(0f, -1f), CharacterFacing.MoveAxis(Keys(Key.S)));
            Assert.Equal(new Vector2(1f, 0f), CharacterFacing.MoveAxis(Keys(Key.D)));
            Assert.Equal(new Vector2(-1f, 0f), CharacterFacing.MoveAxis(Keys(Key.A)));
            Assert.Equal(Vector2.Zero, CharacterFacing.MoveAxis(Keys()));
        }

        [Fact]
        public void IntendedMoveDirection_MatchesTheMoveBasis_AtYawZero()
        {
            // Same basis CharacterController3D moves along: W -> -Z, D -> +X (pinned by CharacterController3DTests).
            Vector3 fwd = CharacterFacing.IntendedMoveDirection(Keys(Key.W), 0f);
            Assert.True(fwd.Z < 0f && MathF.Abs(fwd.X) < 1e-5f && MathF.Abs(fwd.Y) < 1e-5f, fwd.ToString());
            Vector3 right = CharacterFacing.IntendedMoveDirection(Keys(Key.D), 0f);
            Assert.True(right.X > 0f && MathF.Abs(right.Z) < 1e-5f && MathF.Abs(right.Y) < 1e-5f, right.ToString());
            Assert.Equal(Vector3.Zero, CharacterFacing.IntendedMoveDirection(Keys(), 0f));
        }

        [Fact]
        public void IntendedMoveDirection_RotatesWithCameraYaw()
        {
            // At yaw = +pi/2 the camera forward rotates to -X, so W now points -X.
            Vector3 fwd = CharacterFacing.IntendedMoveDirection(Keys(Key.W), MathF.PI / 2f);
            Assert.True(fwd.X < 0f && MathF.Abs(fwd.Z) < 1e-5f, fwd.ToString());
        }

        [Fact]
        public void TurnTowards_ConvergesToTarget_AndThenHoldsExactly()
        {
            Vector3 intended = CharacterFacing.IntendedMoveDirection(Keys(Key.W), 0f);
            float target = CharacterFacing.YawOf(intended);
            float yaw = 0f;
            for (int i = 0; i < 300; i++) yaw = CharacterFacing.TurnTowards(yaw, intended, 12f, 1f / 60f);
            Assert.Equal(target, yaw, 3);
            // Once aligned, the same intended direction produces no further motion (no drift/jitter).
            float held = CharacterFacing.TurnTowards(yaw, intended, 12f, 1f / 60f);
            Assert.Equal(yaw, held, 6);
        }

        [Fact]
        public void TurnTowards_HoldsFacing_WhenNoKeyHeld()
        {
            const float yaw = 1.234f;
            Assert.Equal(yaw, CharacterFacing.TurnTowards(yaw, Vector3.Zero, 12f, 1f / 60f), 6);
        }

        [Fact]
        public void TurnTowards_StepsAtMostMaxRate()
        {
            // Target yaw 0.5 rad; one step at rate 1 rad/s over 0.1 s can move at most 0.1 rad, so it lands at 0.1.
            Vector3 intended = new(MathF.Sin(0.5f), 0f, MathF.Cos(0.5f));
            float stepped = CharacterFacing.TurnTowards(0f, intended, maxTurnRate: 1f, dt: 0.1f);
            Assert.Equal(0.1f, stepped, 4);
        }

        [Fact]
        public void TurnTowards_TakesShortestPath_AcrossTheWrap()
        {
            // Current just under +pi, target just under -pi: the short way is FORWARD through +pi (yaw increases and
            // wraps to the -pi side), not backward almost all the way round.
            float current = 3.0f;                                   // ~+172 deg
            Vector3 intended = new(MathF.Sin(-3.0f), 0f, MathF.Cos(-3.0f));   // target ~-172 deg
            float stepped = CharacterFacing.TurnTowards(current, intended, maxTurnRate: 1f, dt: 0.1f);
            // A 0.1 rad step the short way lands at ~3.1 which wraps to ~-3.083; a wrong long-way step would head to ~2.9.
            Assert.True(stepped < -3.0f || stepped > 3.09f,
                $"expected a shortest-path step across the wrap, got {stepped}");
        }

        [Fact]
        public void YawOf_IsZero_ForADegenerateDirection()
        {
            Assert.Equal(0f, CharacterFacing.YawOf(Vector3.Zero));
            Assert.Equal(0f, CharacterFacing.YawOf(new Vector3(0f, 5f, 0f)));   // pure vertical has no planar heading
        }
    }
}
