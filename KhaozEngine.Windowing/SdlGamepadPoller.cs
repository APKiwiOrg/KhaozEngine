using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid.Sdl2;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Best-effort SDL2 game-controller polling that turns connected pads into <see cref="GamepadState"/>s for
    /// <see cref="AppWindow"/>. Every SDL call is guarded: on any failure (subsystem not available, API
    /// mismatch, no controllers) it yields an empty list, so the window loop is never affected. SDL enum
    /// values are referenced by their stable integer codes rather than Veldrid field names. Unverified without
    /// a physical controller; the <see cref="GamepadState"/> model it produces is headless-tested.
    /// </summary>
    internal sealed class SdlGamepadPoller : IDisposable
    {
        const uint SDL_INIT_GAMECONTROLLER = 0x00002000;

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        static extern int SDL_InitSubSystem(uint flags);

        // (engine button, SDL_CONTROLLER_BUTTON_* code)
        static readonly (GamepadButton Btn, int Code)[] ButtonMap =
        {
            (GamepadButton.A, 0), (GamepadButton.B, 1), (GamepadButton.X, 2), (GamepadButton.Y, 3),
            (GamepadButton.Back, 4), (GamepadButton.Guide, 5), (GamepadButton.Start, 6),
            (GamepadButton.LeftStick, 7), (GamepadButton.RightStick, 8),
            (GamepadButton.LeftShoulder, 9), (GamepadButton.RightShoulder, 10),
            (GamepadButton.DpadUp, 11), (GamepadButton.DpadDown, 12),
            (GamepadButton.DpadLeft, 13), (GamepadButton.DpadRight, 14),
        };

        readonly Dictionary<int, SDL_GameController> _open = new();
        readonly Dictionary<int, HashSet<GamepadButton>> _prevDown = new();
        bool _initTried;

        public IReadOnlyList<GamepadState> Poll()
        {
            try { return PollCore(); }
            catch { return Array.Empty<GamepadState>(); }
        }

        IReadOnlyList<GamepadState> PollCore()
        {
            if (!_initTried) { _initTried = true; try { SDL_InitSubSystem(SDL_INIT_GAMECONTROLLER); } catch { } }

            Sdl2Native.SDL_GameControllerUpdate();
            int count = Sdl2Native.SDL_NumJoysticks();
            if (count <= 0) return Array.Empty<GamepadState>();

            var pads = new List<GamepadState>();
            int player = 0;
            for (int i = 0; i < count; i++)
            {
                if (!Sdl2Native.SDL_IsGameController(i)) continue;
                if (!_open.TryGetValue(i, out var c) || c.NativePointer == IntPtr.Zero)
                {
                    c = Sdl2Native.SDL_GameControllerOpen(i);
                    _open[i] = c;
                }
                if (c.NativePointer == IntPtr.Zero) continue;
                pads.Add(Read(player++, i, c));
            }
            return pads;
        }

        GamepadState Read(int player, int slot, SDL_GameController c)
        {
            var down = new HashSet<GamepadButton>();
            foreach (var (btn, code) in ButtonMap)
                if (Sdl2Native.SDL_GameControllerGetButton(c, (SDL_GameControllerButton)code) != 0)
                    down.Add(btn);

            if (!_prevDown.TryGetValue(slot, out var prev)) prev = new HashSet<GamepadButton>();
            var pressed = new HashSet<GamepadButton>(down);
            pressed.ExceptWith(prev);
            var released = new HashSet<GamepadButton>(prev);
            released.ExceptWith(down);
            _prevDown[slot] = down;

            Vector2 left = new(Axis(c, 0), Axis(c, 1));     // LEFTX, LEFTY
            Vector2 right = new(Axis(c, 2), Axis(c, 3));    // RIGHTX, RIGHTY
            float lt = Trigger(c, 4);                       // TRIGGERLEFT
            float rt = Trigger(c, 5);                       // TRIGGERRIGHT

            return new GamepadState(player, down, pressed, released, left, right, lt, rt);
        }

        // Axis raw is -32768..32767; map to -1..1 (Y down in SDL, keep as-is; consumers flip if needed).
        static float Axis(SDL_GameController c, int code) =>
            Math.Clamp(Sdl2Native.SDL_GameControllerGetAxis(c, (SDL_GameControllerAxis)code) / 32767f, -1f, 1f);

        // Triggers are 0..32767.
        static float Trigger(SDL_GameController c, int code) =>
            Math.Clamp(Sdl2Native.SDL_GameControllerGetAxis(c, (SDL_GameControllerAxis)code) / 32767f, 0f, 1f);

        public void Dispose()
        {
            foreach (var c in _open.Values)
                try { if (c.NativePointer != IntPtr.Zero) Sdl2Native.SDL_GameControllerClose(c); } catch { }
            _open.Clear();
        }
    }
}
