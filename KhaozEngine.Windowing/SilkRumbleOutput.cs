using System;
using System.Collections.Generic;
using Silk.NET.Input;
using KhaozEngine.Windowing.Rumble;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The Silk.NET rumble OUTPUT sink for <see cref="AppWindow"/>: writes mixed motor levels onto a connected
    /// Silk <see cref="IGamepad"/>'s <see cref="IGamepad.VibrationMotors"/>. This is the ONE place in the engine
    /// that touches the Silk vibration surface (the mirror of the AppWindow-only input-static rule). Every write is
    /// guarded so a flaky pad or an unsupported backend can never break the frame loop.
    /// </summary>
    /// <remarks>
    /// Motor convention: Silk exposes an ordered <see cref="IGamepad.VibrationMotors"/> list. By the XInput/SDL
    /// convention motor 0 is the low-frequency (heavy/left) motor and motor 1 the high-frequency (light/right)
    /// motor; a pad exposing a single motor gets the max of the two so a rumble is still felt. Setting
    /// <see cref="IMotor.Speed"/> is the whole API; Silk pushes it to the driver. Some backends truncate variable
    /// intensity (documented on <see cref="IMotor.Speed"/>).
    /// <para>
    /// Backend reality: the current GLFW input backend enumerates ZERO vibration motors (GLFW has no haptics API),
    /// so <see cref="Set"/> finds an empty motor list and no-ops gracefully. The wiring is correct and a future
    /// SDL-backed window would light up through this exact sink with no game-code change.
    /// </para>
    /// </remarks>
    internal sealed class SilkRumbleOutput : IRumbleOutput
    {
        readonly IInputContext _input;

        public SilkRumbleOutput(IInputContext input)
        {
            _input = input;
        }

        /// <inheritdoc/>
        public void Set(PlayerIndex player, float lowFrequency, float highFrequency)
        {
            try
            {
                IGamepad? pad = ResolvePad((int)player);
                if (pad == null) return;
                IReadOnlyList<IMotor> motors = pad.VibrationMotors;
                int count = motors.Count;
                if (count == 0) return; // GLFW backend: no motors, graceful no-op.

                if (count == 1)
                {
                    // Single-motor pad: drive it with whichever channel is stronger so a rumble is still felt.
                    motors[0].Speed = Math.Max(lowFrequency, highFrequency);
                    return;
                }

                motors[0].Speed = lowFrequency;   // heavy / low-frequency (left)
                motors[1].Speed = highFrequency;  // light / high-frequency (right)
                // Any further motors (rare) are left untouched.
            }
            catch { /* a flaky pad must never break the loop */ }
        }

        /// <summary>
        /// Map a 0-based player slot to the Nth CONNECTED Silk gamepad, matching how <see cref="SilkGamepadReader"/>
        /// assigns player indices (connected pads in order, skipping disconnected ones). Returns null if there is no
        /// such connected pad.
        /// </summary>
        IGamepad? ResolvePad(int player)
        {
            IReadOnlyList<IGamepad> pads = _input.Gamepads;
            int seen = 0;
            for (int i = 0; i < pads.Count; i++)
            {
                IGamepad pad = pads[i];
                if (pad == null || !pad.IsConnected) continue;
                if (seen == player) return pad;
                seen++;
            }
            return null;
        }
    }
}
