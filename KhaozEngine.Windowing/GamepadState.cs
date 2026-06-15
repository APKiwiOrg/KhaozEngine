using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>Engine-native gamepad buttons (SDL GameController layout; no Veldrid/SDL types leak out).</summary>
    public enum GamepadButton
    {
        A, B, X, Y,
        LeftShoulder, RightShoulder,
        Back, Start, Guide,
        LeftStick, RightStick,
        DpadUp, DpadDown, DpadLeft, DpadRight,
    }

    /// <summary>Radial stick deadzone math, shared so stick handling is consistent and unit-testable.</summary>
    public static class Deadzone
    {
        /// <summary>
        /// Apply a radial deadzone: zero the stick when its magnitude is within <paramref name="deadzone"/>,
        /// otherwise rescale so the deadzone edge maps to 0 and full tilt to 1 (direction preserved). Radial
        /// (magnitude-based), not per-axis, so a small diagonal drift is rejected as a whole.
        /// </summary>
        public static Vector2 Radial(Vector2 stick, float deadzone)
        {
            float mag = stick.Length();
            if (mag <= deadzone || mag <= 0f) return Vector2.Zero;
            float scaled = MathF.Min(1f, (mag - deadzone) / (1f - deadzone));
            return stick / mag * scaled;
        }
    }

    /// <summary>
    /// Immutable per-frame snapshot of one gamepad: which buttons are held / went down / went up this frame,
    /// the two analog sticks (raw, -1..1), and the two triggers (0..1). Built frame-by-frame like
    /// <see cref="InputState"/>, so it is headless-testable; <see cref="AppWindow"/> fills it from SDL when a
    /// controller is connected. Use <see cref="LeftStickDeadzoned"/>/<see cref="RightStickDeadzoned"/> rather
    /// than the raw sticks for movement.
    /// </summary>
    public sealed class GamepadState
    {
        /// <summary>A not-connected pad: nothing held, zero axes.</summary>
        public static readonly GamepadState Disconnected = new(
            -1, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 0f, connected: false);

        /// <summary>Player/controller index (0-based); -1 when disconnected.</summary>
        public int Index { get; }
        /// <summary>True when this slot has a connected controller.</summary>
        public bool IsConnected { get; }

        public IReadOnlySet<GamepadButton> ButtonsDown { get; }
        public IReadOnlySet<GamepadButton> ButtonsPressed { get; }
        public IReadOnlySet<GamepadButton> ButtonsReleased { get; }
        public Vector2 LeftStick { get; }
        public Vector2 RightStick { get; }
        public float LeftTrigger { get; }
        public float RightTrigger { get; }

        public GamepadState(
            int index, IReadOnlySet<GamepadButton> down, IReadOnlySet<GamepadButton> pressed,
            IReadOnlySet<GamepadButton> released, Vector2 leftStick, Vector2 rightStick,
            float leftTrigger, float rightTrigger, bool connected = true)
        {
            Index = index;
            IsConnected = connected;
            ButtonsDown = down; ButtonsPressed = pressed; ButtonsReleased = released;
            LeftStick = leftStick; RightStick = rightStick;
            LeftTrigger = leftTrigger; RightTrigger = rightTrigger;
        }

        public bool IsDown(GamepadButton button) => ButtonsDown.Contains(button);
        public bool WasPressed(GamepadButton button) => ButtonsPressed.Contains(button);
        public bool WasReleased(GamepadButton button) => ButtonsReleased.Contains(button);

        /// <summary>Left stick with a radial deadzone applied (see <see cref="Deadzone.Radial"/>).</summary>
        public Vector2 LeftStickDeadzoned(float deadzone = 0.15f) => Deadzone.Radial(LeftStick, deadzone);
        /// <summary>Right stick with a radial deadzone applied.</summary>
        public Vector2 RightStickDeadzoned(float deadzone = 0.15f) => Deadzone.Radial(RightStick, deadzone);
    }
}
