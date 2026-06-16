using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Reads connected Silk.NET gamepads into engine <see cref="GamepadState"/>s for <see cref="AppWindow"/>,
    /// the Silk replacement for the previous controller poller. Every read is guarded: on any failure (a flaky controller,
    /// an API hiccup) it yields what it can and never lets the window loop break. Maintains a per-pad previous
    /// down-set so it can produce pressed/released edges frame-by-frame, mirroring the old poller. The
    /// <see cref="GamepadState"/> model it produces is headless-tested.
    /// </summary>
    internal sealed class SilkGamepadReader
    {
        // Per-pad (keyed by Silk gamepad index) previous-frame down set, for pressed/released edge detection.
        readonly Dictionary<int, HashSet<GamepadButton>> _prevDown = new();

        public IReadOnlyList<GamepadState> Read(IReadOnlyList<IGamepad> gamepads)
        {
            try { return ReadCore(gamepads); }
            catch { return Array.Empty<GamepadState>(); }
        }

        IReadOnlyList<GamepadState> ReadCore(IReadOnlyList<IGamepad> gamepads)
        {
            if (gamepads == null || gamepads.Count == 0) return Array.Empty<GamepadState>();

            var pads = new List<GamepadState>();
            int player = 0;
            for (int i = 0; i < gamepads.Count; i++)
            {
                IGamepad pad = gamepads[i];
                if (pad == null) continue;
                try
                {
                    if (!pad.IsConnected) continue;
                    pads.Add(ReadOne(player++, pad));
                }
                catch { /* skip a flaky pad, keep the rest */ }
            }
            return pads.Count == 0 ? Array.Empty<GamepadState>() : pads;
        }

        GamepadState ReadOne(int player, IGamepad pad)
        {
            int slot = pad.Index;

            var down = new HashSet<GamepadButton>();
            foreach (Button b in pad.Buttons)
                if (b.Pressed && MapButton(b.Name, out GamepadButton g))
                    down.Add(g);

            if (!_prevDown.TryGetValue(slot, out var prev)) prev = new HashSet<GamepadButton>();
            var pressed = new HashSet<GamepadButton>(down);
            pressed.ExceptWith(prev);
            var released = new HashSet<GamepadButton>(prev);
            released.ExceptWith(down);
            _prevDown[slot] = down;

            Vector2 left = Vector2.Zero, right = Vector2.Zero;
            var sticks = pad.Thumbsticks;
            if (sticks.Count > 0) left = new Vector2(sticks[0].X, sticks[0].Y);
            if (sticks.Count > 1) right = new Vector2(sticks[1].X, sticks[1].Y);

            float lt = 0f, rt = 0f;
            var triggers = pad.Triggers;
            if (triggers.Count > 0) lt = triggers[0].Position;
            if (triggers.Count > 1) rt = triggers[1].Position;

            return new GamepadState(player, down, pressed, released, left, right, lt, rt);
        }

        static bool MapButton(ButtonName name, out GamepadButton r)
        {
            switch (name)
            {
                case ButtonName.A: r = GamepadButton.A; return true;
                case ButtonName.B: r = GamepadButton.B; return true;
                case ButtonName.X: r = GamepadButton.X; return true;
                case ButtonName.Y: r = GamepadButton.Y; return true;
                case ButtonName.Back: r = GamepadButton.Back; return true;
                case ButtonName.Start: r = GamepadButton.Start; return true;
                case ButtonName.Home: r = GamepadButton.Guide; return true;
                case ButtonName.LeftStick: r = GamepadButton.LeftStick; return true;
                case ButtonName.RightStick: r = GamepadButton.RightStick; return true;
                case ButtonName.LeftBumper: r = GamepadButton.LeftShoulder; return true;
                case ButtonName.RightBumper: r = GamepadButton.RightShoulder; return true;
                case ButtonName.DPadUp: r = GamepadButton.DpadUp; return true;
                case ButtonName.DPadDown: r = GamepadButton.DpadDown; return true;
                case ButtonName.DPadLeft: r = GamepadButton.DpadLeft; return true;
                case ButtonName.DPadRight: r = GamepadButton.DpadRight; return true;
                default: r = default; return false;
            }
        }
    }
}
