namespace KhaozEngine.Windowing.Rumble
{
    /// <summary>
    /// The low-level rumble OUTPUT primitive: set a player's two motor levels (already mixed and clamped to [0,1])
    /// on the physical device. This is the ONLY seam the Silk.NET/GLFW motor code sits behind, so the pure
    /// <see cref="RumbleMixer"/> and the game-facing <see cref="RumbleDriver"/> stay device-free and headless-testable.
    /// <see cref="AppWindow"/> supplies the Silk implementation; tests supply a recording fake; a headless server
    /// uses the no-op.
    /// </summary>
    public interface IRumbleOutput
    {
        /// <summary>
        /// Drive player <paramref name="player"/>'s motors to <paramref name="lowFrequency"/> (heavy/left) and
        /// <paramref name="highFrequency"/> (light/right), both in [0,1]. Implementations must never throw; a
        /// disconnected or motor-less pad is a graceful no-op.
        /// </summary>
        void Set(PlayerIndex player, float lowFrequency, float highFrequency);
    }

    /// <summary>A rumble output that goes nowhere. Backs headless tests and servers with no gamepad.</summary>
    public sealed class NoopRumbleOutput : IRumbleOutput
    {
        /// <summary>Shared instance (stateless).</summary>
        public static readonly NoopRumbleOutput Instance = new();

        /// <inheritdoc/>
        public void Set(PlayerIndex player, float lowFrequency, float highFrequency) { }
    }
}
