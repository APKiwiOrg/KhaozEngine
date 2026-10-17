using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Hands input back to the game after a hold without a press the game did not see begin.
    /// <see cref="Pointer"/> and <see cref="InputManager"/> derive mouse presses and stick steps from down
    /// transitions, and a held frame reports nothing down, so a button or stick still down when the hold ends would
    /// read as fresh. On that first unheld frame every mouse button still down is hidden until its physical release,
    /// and every gamepad with a button down, or a stick or trigger past <see cref="InputManager.StickThreshold"/>,
    /// reads as neutral until it is idle. The frame a hidden input lets go is hidden too, so its release edge never shows.
    /// Keys need nothing, because their press edge comes from the window. This is the input-level form of
    /// <see cref="Pointer"/>'s suppress-until-release, so it covers every consumer, game-owned pointers included.
    /// A frame hiding nothing is returned as is and allocates nothing.
    /// </summary>
    internal sealed class HeldInputRelease
    {
        static readonly IReadOnlySet<GamepadButton> NoButtons = new HashSet<GamepadButton>();
        static readonly MouseButton[] AllButtons = Enum.GetValues<MouseButton>();

        int _mouse;
        int _pads;

        /// <summary>
        /// Filter an unheld frame. <paramref name="holdEnded"/> is true on the first unheld frame after a held one.
        /// </summary>
        internal InputState Apply(InputState input, bool holdEnded)
        {
            IReadOnlyList<GamepadState> pads = input.Gamepads;
            if (holdEnded)
            {
                _mouse |= Mask(input.MouseDown) | Mask(input.MouseReleased);
                for (int i = 0; i < pads.Count && i < 32; i++)
                    if (Active(pads[i]) || pads[i].ButtonsReleased.Count > 0) _pads |= 1 << i;
            }
            if (_mouse == 0 && _pads == 0) return input;

            int hideMouse = _mouse & (Mask(input.MouseDown) | Mask(input.MousePressed) | Mask(input.MouseReleased));
            int hidePads = _pads & (pads.Count >= 32 ? -1 : (1 << pads.Count) - 1);

            // Keep hiding only what is still down. A released button or an idle pad reaches the game next frame.
            _mouse &= Mask(input.MouseDown);
            for (int i = 0; i < 32; i++)
                if ((_pads & (1 << i)) != 0 && (i >= pads.Count || !Active(pads[i]))) _pads &= ~(1 << i);

            if (hideMouse == 0 && hidePads == 0) return input;
            return new InputState(input.Gestures, input.HorizontalScrollDelta, input.MouseDeltaPoints,
                input.FramebufferScale, input.KeysDown, input.KeysPressed, input.KeysReleased,
                Without(input.MouseDown, hideMouse), Without(input.MousePressed, hideMouse), input.MousePosition,
                input.MouseDelta, input.ScrollDelta, input.Width, input.Height, Neutral(pads, hidePads),
                input.Touches, input.WindowFocused, input.KeysRepeated, Without(input.MouseReleased, hideMouse),
                input.TextInput, input.TextInputAvailable, input.PointerCaptured);
        }

        // A pad is idle once no button is down and both raw sticks and both triggers are within the threshold. The
        // raw length bounds the deadzoned per-axis value InputManager steps on, so an idle pad cannot step, and a
        // trigger bound as a button cannot fire a press the game did not see begin.
        static bool Active(GamepadState pad) =>
            pad.ButtonsDown.Count > 0 ||
            pad.LeftStick.Length() > InputManager.StickThreshold ||
            pad.RightStick.Length() > InputManager.StickThreshold ||
            pad.LeftTrigger > InputManager.StickThreshold ||
            pad.RightTrigger > InputManager.StickThreshold;

        static int Mask(IReadOnlySet<MouseButton> buttons)
        {
            int mask = 0;
            foreach (MouseButton button in AllButtons)
                if (buttons.Contains(button)) mask |= 1 << (int)button;
            return mask;
        }

        static IReadOnlySet<MouseButton> Without(IReadOnlySet<MouseButton> buttons, int hide)
        {
            if ((Mask(buttons) & hide) == 0) return buttons;
            var kept = new HashSet<MouseButton>();
            foreach (MouseButton button in AllButtons)
                if ((hide & (1 << (int)button)) == 0 && buttons.Contains(button)) kept.Add(button);
            return kept;
        }

        static IReadOnlyList<GamepadState> Neutral(IReadOnlyList<GamepadState> pads, int hide)
        {
            if (hide == 0) return pads;
            var result = new GamepadState[pads.Count];
            for (int i = 0; i < pads.Count; i++)
            {
                GamepadState pad = pads[i];
                result[i] = (hide & (1 << i)) == 0 ? pad
                    : new GamepadState(pad.Index, NoButtons, NoButtons, NoButtons, Vector2.Zero, Vector2.Zero, 0f, 0f,
                        pad.IsConnected);
            }
            return result;
        }
    }
}
