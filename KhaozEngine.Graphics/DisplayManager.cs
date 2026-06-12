using System;

namespace KhaozEngine.Graphics;

public sealed partial class DisplayManager
{
    /// <summary>Maps a <see cref="WindowMode"/> to MonoGame's
    /// (<c>IsFullScreen</c>, <c>HardwareModeSwitch</c>) pair.</summary>
    internal static (bool isFullScreen, bool hardwareModeSwitch) ResolveMode(WindowMode mode) => mode switch
    {
        WindowMode.Windowed             => (false, true),
        WindowMode.BorderlessFullscreen => (true,  false),
        WindowMode.ExclusiveFullscreen  => (true,  true),
        _                               => (false, true),
    };

    /// <summary>Clamps a requested client size up to the per-axis minimum (0 = no floor).</summary>
    internal static Microsoft.Xna.Framework.Point ClampToMinimum(
        Microsoft.Xna.Framework.Point requested, int minWidth, int minHeight) =>
        new(Math.Max(requested.X, minWidth), Math.Max(requested.Y, minHeight));
}
