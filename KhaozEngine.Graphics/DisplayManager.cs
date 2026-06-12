using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Centralizes MonoGame display/window configuration: backbuffer size, fullscreen mode,
/// resizing + minimum-size floor, orientations, and title. Takes the live
/// <see cref="GraphicsDeviceManager"/> and <see cref="GameWindow"/> ("takes what it needs,
/// no statics"). Construct in the <c>Game</c> constructor; the constructor sets preferences
/// (no <c>ApplyChanges</c>, the normal pre-device path) and runtime mutators apply immediately.
/// </summary>
public sealed partial class DisplayManager
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly GameWindow _window;
    private bool _floorWired;
    private bool _inResize;

    /// <summary>Current applied settings.</summary>
    public DisplaySettings Settings { get; private set; }

    /// <summary>Current preferred backbuffer width.</summary>
    public int Width => _graphics.PreferredBackBufferWidth;

    /// <summary>Current preferred backbuffer height.</summary>
    public int Height => _graphics.PreferredBackBufferHeight;

    /// <summary>Current preferred backbuffer size as a point.</summary>
    public Point Size => new(Width, Height);

    /// <summary>True when the current mode is any fullscreen mode.</summary>
    public bool IsFullscreen => Settings.Mode != WindowMode.Windowed;

    /// <summary>Wraps the device + window and applies the initial settings (preferences only).</summary>
    public DisplayManager(GraphicsDeviceManager graphics, GameWindow window, DisplaySettings settings)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ApplyInternal(settings, applyChanges: false);
    }

    /// <summary>Applies new settings and commits them to the device (<c>ApplyChanges</c>).</summary>
    public void Apply(DisplaySettings settings) =>
        ApplyInternal(settings ?? throw new ArgumentNullException(nameof(settings)), applyChanges: true);

    /// <summary>Sets the backbuffer resolution at runtime.</summary>
    public void SetResolution(int width, int height) =>
        Apply(Settings with { Width = width, Height = height });

    /// <summary>Sets the presentation mode at runtime.</summary>
    public void SetMode(WindowMode mode) => Apply(Settings with { Mode = mode });

    /// <summary>Toggles between windowed and borderless fullscreen.</summary>
    public void ToggleFullscreen() =>
        SetMode(IsFullscreen ? WindowMode.Windowed : WindowMode.BorderlessFullscreen);

    /// <summary>Sets resizing and the optional minimum-size floor (0 = no floor).</summary>
    public void SetResizable(bool allow, int minWidth = 0, int minHeight = 0) =>
        Apply(Settings with { AllowUserResizing = allow, MinWidth = minWidth, MinHeight = minHeight });

    private void ApplyInternal(DisplaySettings settings, bool applyChanges)
    {
        Settings = settings;

        _graphics.PreferredBackBufferWidth = settings.Width;
        _graphics.PreferredBackBufferHeight = settings.Height;

        var (isFullScreen, hardwareModeSwitch) = ResolveMode(settings.Mode);
        _graphics.IsFullScreen = isFullScreen;
        _graphics.HardwareModeSwitch = hardwareModeSwitch;
        _graphics.SupportedOrientations = settings.SupportedOrientations;

        _window.AllowUserResizing = settings.AllowUserResizing;
        if (settings.Title is not null) _window.Title = settings.Title;

        if (!_floorWired)
        {
            _window.ClientSizeChanged += OnClientSizeChanged;
            _floorWired = true;
        }

        if (applyChanges) _graphics.ApplyChanges();
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_inResize) return;
        if (!Settings.AllowUserResizing) return;
        if (Settings.MinWidth <= 0 && Settings.MinHeight <= 0) return;

        Rectangle bounds = _window.ClientBounds;
        Point clamped = ClampToMinimum(new Point(bounds.Width, bounds.Height),
            Settings.MinWidth, Settings.MinHeight);
        if (clamped.X == bounds.Width && clamped.Y == bounds.Height) return;

        _inResize = true;
        _graphics.PreferredBackBufferWidth = clamped.X;
        _graphics.PreferredBackBufferHeight = clamped.Y;
        _graphics.ApplyChanges();
        _inResize = false;
    }

    /// <summary>Maps a <see cref="WindowMode"/> to MonoGame's
    /// (<c>IsFullScreen</c>, <c>HardwareModeSwitch</c>) pair.</summary>
    internal static (bool isFullScreen, bool hardwareModeSwitch) ResolveMode(WindowMode mode) => mode switch
    {
        WindowMode.Windowed             => (false, true),
        WindowMode.BorderlessFullscreen => (true,  false),
        WindowMode.ExclusiveFullscreen  => (true,  true),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Clamps a requested client size up to the per-axis minimum (0 = no floor).</summary>
    internal static Microsoft.Xna.Framework.Point ClampToMinimum(
        Microsoft.Xna.Framework.Point requested, int minWidth, int minHeight) =>
        new(Math.Max(requested.X, minWidth), Math.Max(requested.Y, minHeight));
}
