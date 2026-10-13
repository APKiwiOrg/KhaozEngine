using System;
using System.Numerics;
using Silk.NET.Input;

namespace KhaozEngine.Windowing;

public sealed partial class AppWindow
{
    NativeGestureRegistration? _nativeGestures;

    /// <summary>First native gesture setup, teardown or callback failure, or null for normal operation and fallback.</summary>
    public NativeGestureFailure? NativeGestureFailure => _nativeGestures?.Failure;

    void WireNativeGestures()
    {
        nint window = 0;
        INativeGestureRegistration? backend = null;
        if (OperatingSystem.IsMacOS())
        {
            window = _window.Native?.Cocoa ?? 0;
            if (window != 0) backend = new MacOSGestureRegistration(this, window);
        }
        else if (OperatingSystem.IsWindows())
        {
            window = _window.Native?.Win32?.Hwnd ?? 0;
            if (window != 0) backend = new WindowsGestureRegistration(this, window);
        }
        _nativeGestures = new NativeGestureRegistration(window, backend, _accumulator,
            () => _accumulator.IsFocused && !_pointerCaptured);
    }

    void RecordWheel(IMouse mouse, float x, float y)
    {
        var delta = new Vector2(x, y);
        if (_nativeGestures == null) { _accumulator.OnScroll(delta); return; }
        if (_nativeGestures.IsInstalled)
        {
            _nativeGestures.OnSilkScroll(delta, Vector2.Zero, false);
            return;
        }
        // Silk keeps its legacy units. An installed native producer supplies the ordered sample.
        bool command = false;
        foreach (var keyboard in _input.Keyboards)
            command |= keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlLeft)
                || keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlRight)
                || keyboard.IsKeyPressed(Silk.NET.Input.Key.SuperLeft)
                || keyboard.IsKeyPressed(Silk.NET.Input.Key.SuperRight);
        _nativeGestures.OnSilkScroll(delta, ToFramebuffer(mouse.Position), command);
    }
}
