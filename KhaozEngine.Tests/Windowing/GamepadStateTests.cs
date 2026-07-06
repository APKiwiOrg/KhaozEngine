using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless gamepad state: button down/pressed/released sets, raw sticks/triggers, and radial-deadzone
    /// stick helpers. Built frame-by-frame like the rest of the input model (no device needed); a live
    /// controller is only required for an integration smoke, not for this logic.
    /// </summary>
    public class GamepadStateTests
    {
        static GamepadState Pad(
            IReadOnlySet<GamepadButton>? down = null, IReadOnlySet<GamepadButton>? pressed = null,
            IReadOnlySet<GamepadButton>? released = null, Vector2 left = default, Vector2 right = default,
            float lt = 0, float rt = 0) =>
            new(0, down ?? new HashSet<GamepadButton>(), pressed ?? new HashSet<GamepadButton>(),
                released ?? new HashSet<GamepadButton>(), left, right, lt, rt);

        [Fact]
        public void Disconnected_HasNothingHeldAndZeroAxes()
        {
            var g = GamepadState.Disconnected;
            Assert.False(g.IsConnected);
            Assert.False(g.IsDown(GamepadButton.A));
            Assert.Equal(Vector2.Zero, g.LeftStick);
            Assert.Equal(0f, g.RightTrigger);
        }

        [Fact]
        public void ConstructedPad_IsConnected_AndReportsButtonEdges()
        {
            var g = Pad(
                down: new HashSet<GamepadButton> { GamepadButton.A },
                pressed: new HashSet<GamepadButton> { GamepadButton.A },
                released: new HashSet<GamepadButton> { GamepadButton.B });

            Assert.True(g.IsConnected);
            Assert.True(g.IsDown(GamepadButton.A));
            Assert.True(g.WasPressed(GamepadButton.A));
            Assert.False(g.WasPressed(GamepadButton.B));
            Assert.True(g.WasReleased(GamepadButton.B));
        }

        [Fact]
        public void RawSticksAndTriggers_ArePreserved()
        {
            var g = Pad(left: new Vector2(0.5f, -0.25f), right: new Vector2(-1f, 1f), lt: 0.3f, rt: 0.8f);
            Assert.Equal(new Vector2(0.5f, -0.25f), g.LeftStick);
            Assert.Equal(new Vector2(-1f, 1f), g.RightStick);
            Assert.Equal(0.3f, g.LeftTrigger);
            Assert.Equal(0.8f, g.RightTrigger);
        }

        [Fact]
        public void DeadzonedStick_ZeroesSmallDriftInsideTheDeadzone()
        {
            var g = Pad(left: new Vector2(0.1f, 0f));
            Assert.Equal(Vector2.Zero, g.LeftStickDeadzoned(0.25f));
        }

        [Fact]
        public void DeadzonedStick_RescalesBeyondTheDeadzone_FullTiltStaysOne()
        {
            // straight right at full tilt stays 1 after rescale
            var g = Pad(left: new Vector2(1f, 0f));
            var v = g.LeftStickDeadzoned(0.25f);
            Assert.Equal(1f, v.X, 4);
            Assert.Equal(0f, v.Y, 4);
        }

        [Fact]
        public void DeadzonedStick_HalfwayBeyondDeadzone_RescalesProportionally()
        {
            // magnitude 0.5, deadzone 0.25 -> (0.5-0.25)/(1-0.25) = 0.3333 along +x
            var g = Pad(left: new Vector2(0.5f, 0f));
            var v = g.LeftStickDeadzoned(0.25f);
            Assert.Equal(0.3333f, v.X, 3);
        }

        [Fact]
        public void Deadzone_RadialHelper_UsesMagnitudeNotPerAxis()
        {
            // a diagonal whose magnitude (~0.283) is below the 0.3 deadzone is zeroed, even though no single
            // axis is tiny: a per-axis deadzone would wrongly keep it.
            var v = Deadzone.Radial(new Vector2(0.2f, 0.2f), 0.3f);
            Assert.Equal(Vector2.Zero, v);
        }
    }
}
