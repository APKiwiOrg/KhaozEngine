namespace KhaozEngine.Graphics;

/// <summary>
/// How the window presents. <see cref="BorderlessFullscreen"/> is windowed fullscreen
/// (no mode switch); <see cref="ExclusiveFullscreen"/> switches the hardware display mode.
/// </summary>
public enum WindowMode
{
    /// <summary>Bordered window at the configured backbuffer size.</summary>
    Windowed,
    /// <summary>Borderless window covering the display (no hardware mode switch).</summary>
    BorderlessFullscreen,
    /// <summary>Exclusive fullscreen with a hardware display-mode switch.</summary>
    ExclusiveFullscreen,
}
