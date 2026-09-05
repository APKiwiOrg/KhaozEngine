using System.Collections.Generic;

namespace KhaozEngine.Windowing
{
    /// <summary>Which monitor a launch places the window on. The zero value is <see cref="Saved"/>, so a
    /// default-constructed options struct takes no engine action at all and whatever the game restored from its
    /// own settings stands.</summary>
    public enum InitialMonitorKind : byte
    {
        /// <summary>Leave the launch placement to the game (the zero value): the engine moves nothing, so a saved
        /// position restored through <see cref="IDisplaySettings.ApplyDisplay"/> is what the player sees.</summary>
        Saved = 0,
        /// <summary>The primary monitor, which is index 0 (GLFW enumerates the primary monitor first).</summary>
        Primary = 1,
        /// <summary>The monitor with the greatest x origin (see <see cref="WindowPlacement.RightmostIndex"/>).</summary>
        Rightmost = 2,
        /// <summary>The monitor with the least x origin (see <see cref="WindowPlacement.LeftmostIndex"/>).</summary>
        Leftmost = 3,
        /// <summary>An explicit index into <see cref="IDisplaySettings.Monitors"/>.</summary>
        Index = 4,
    }

    /// <summary>
    /// Where a launch puts the window: <see cref="Saved"/> (the default, no engine action), <see cref="Primary"/>,
    /// <see cref="Rightmost"/>, <see cref="Leftmost"/>, or <see cref="At"/> an explicit index. This is the consumer's
    /// REQUEST. <see cref="Resolve"/> turns it into a concrete index into a live monitor list, or -1 for "do
    /// nothing", which is the answer for <see cref="Saved"/>, for an empty list (headless) and for an index that
    /// names no connected monitor.
    /// <para>Pure and headless-testable, in the same shape as <see cref="FrameCap"/>. Only <see cref="AppWindow"/>
    /// supplies the live monitor list, and it applies the result through
    /// <see cref="IDisplaySettings.MoveToMonitor"/>, which already centres the window (or re-covers the monitor when
    /// borderless fullscreen).</para>
    /// </summary>
    public readonly record struct InitialMonitor
    {
        /// <summary>Which kind of placement this value carries.</summary>
        public InitialMonitorKind Kind { get; private init; }

        /// <summary>The monitor index for <see cref="InitialMonitorKind.Index"/> (0 for every other kind).</summary>
        public int Index { get; private init; }

        /// <summary>No engine action: the game's own boot restore owns the placement. The default value.</summary>
        public static InitialMonitor Saved => default;

        /// <summary>The primary monitor (index 0).</summary>
        public static InitialMonitor Primary => new() { Kind = InitialMonitorKind.Primary };

        /// <summary>The monitor furthest right on the virtual desktop.</summary>
        public static InitialMonitor Rightmost => new() { Kind = InitialMonitorKind.Rightmost };

        /// <summary>The monitor furthest left on the virtual desktop.</summary>
        public static InitialMonitor Leftmost => new() { Kind = InitialMonitorKind.Leftmost };

        /// <summary>The monitor at <paramref name="index"/> into <see cref="IDisplaySettings.Monitors"/>. A negative
        /// index is <see cref="Saved"/> (there is nothing to place on), and an index past the end resolves to -1 at
        /// <see cref="Resolve"/> time rather than throwing, because a monitor can be unplugged between launches.</summary>
        public static InitialMonitor At(int index)
            => index >= 0 ? new() { Kind = InitialMonitorKind.Index, Index = index } : Saved;

        /// <summary>True when this asks for nothing (the default), so the game's restored placement stands.</summary>
        public bool IsSaved => Kind == InitialMonitorKind.Saved;

        /// <summary>
        /// The index into <paramref name="monitors"/> this request names, or -1 when the engine should move nothing:
        /// <see cref="Saved"/>, an empty list, or an index no longer connected. Pure.
        /// </summary>
        public int Resolve(IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return -1;
            return Kind switch
            {
                InitialMonitorKind.Primary => 0,
                InitialMonitorKind.Rightmost => WindowPlacement.RightmostIndex(monitors),
                InitialMonitorKind.Leftmost => WindowPlacement.LeftmostIndex(monitors),
                InitialMonitorKind.Index => Index < monitors.Count ? Index : -1,
                _ => -1,
            };
        }
    }
}
