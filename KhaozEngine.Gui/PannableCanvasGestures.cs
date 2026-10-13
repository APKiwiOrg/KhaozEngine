using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

public sealed partial class PannableCanvas
{
    PannableCanvasGestures? _gestures;
    PannableCanvasGestures Gestures => _gestures ??= new(this);

    /// <summary>The frame-aware controller. Call <see cref="CancelGestures"/> when hiding or replacing this view.</summary>
    public PanZoomController GestureController => Gestures.Controller;

    /// <summary>
    /// Frame-aware gesture policy, including slop, inertia, keyboard navigation and wheel animation duration.
    /// Each update maps wheel mode, speed, step and snap resolution from <see cref="Wheel"/>,
    /// <see cref="ScrollPanSpeed"/>, <see cref="ZoomStep"/> and <see cref="SnapZoomLevels"/>.
    /// </summary>
    public PanZoomOptions GestureOptions => GestureController.Options;

    /// <summary>
    /// Applies full-frame gestures and clamps the camera. Update overlays first so their blocked regions or
    /// <paramref name="isBlocked"/> predicate participate in admission. Reserves this viewport after admission
    /// when <see cref="BlockInput"/> is true. Use <see cref="TryGetGestureTap"/> for accepted single taps.
    /// Ctrl/Cmd+wheel overrides Pan mode and uses configured snap stops. Native magnify and command+precise
    /// zoom bypass wheel snap stops. Keyboard navigation requires both
    /// enabled gesture options and <paramref name="hasKeyboardFocus"/>.
    /// </summary>
    public void Update(InputManager input, float dtSeconds, bool hasKeyboardFocus = false,
        Func<Vector2, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        Gestures.Update(input, dtSeconds, hasKeyboardFocus, isBlocked);
        if (BlockInput) input.Pointer.BlockRegion(Viewport);
        Clamp();
    }

    /// <summary>Discards inertia, pending wheel zoom and tap history. Held contacts must release before restarting.</summary>
    public void CancelGestures() => _gestures?.Controller.Cancel();

    /// <summary>
    /// Returns the accepted single tap from the latest frame-aware update, in world coordinates.
    /// Drag releases are excluded. A single tap reports immediately, and a qualifying second tap zooms
    /// instead of reporting another single tap. Use <see cref="TryGetTap"/> with the legacy update path.
    /// </summary>
    public bool TryGetGestureTap(out Vector2 world)
    {
        if (_gestures?.Controller.Tapped == true)
        {
            world = ScreenToWorld(_gestures.Controller.TapPosition);
            return true;
        }
        world = default;
        return false;
    }

    /// <summary>Owns frame-aware recognition and adapts Pointer-space effects to the canvas camera.</summary>
    sealed class PannableCanvasGestures : IPanZoomTarget
    {
        readonly PannableCanvas _canvas;
        readonly Func<float, float, float> _snapResolver;
        internal PanZoomController Controller { get; } = new();

        internal PannableCanvasGestures(PannableCanvas canvas)
        {
            _canvas = canvas;
            _snapResolver = ResolveSnap;
            MapWheelPolicy();
        }

        internal void Update(InputManager input, float dtSeconds, bool hasKeyboardFocus,
            Func<Vector2, bool>? isBlocked)
        {
            MapWheelPolicy();
            Controller.Update(input.Pointer, input.State, this, _canvas.Viewport, dtSeconds,
                input.PointerInputSuppressed || !_canvas.EnablePan, hasKeyboardFocus, isBlocked);
        }

        void MapWheelPolicy()
        {
            var options = Controller.Options;
            options.WheelMode = _canvas.Wheel == CanvasWheelMode.Zoom ? PanZoomWheelMode.Zoom : PanZoomWheelMode.Pan;
            options.WheelPanSpeed = _canvas.ScrollPanSpeed;
            options.WheelZoomFactor = _canvas.ZoomStep;
            options.WheelZoomFactorResolver = _canvas.SnapZoomLevels is { Length: > 0 } ? _snapResolver : null;
        }

        float ResolveSnap(float wheelDelta, float _)
        {
            float destination = Math.Clamp(_canvas.TargetZoom(wheelDelta), _canvas.MinZoom, _canvas.MaxZoom);
            return destination / _canvas.Camera.Zoom;
        }

        public void PanByScreenDelta(Vector2 delta)
        {
            if (!_canvas.EnablePan) return;
            _canvas.Camera.PanByScreenDelta(delta);
            _canvas.Clamp();
        }

        public void ZoomByFactor(float factor, Vector2 anchor)
        {
            if (!_canvas.EnablePan) return;
            _canvas.ZoomToCursor(_canvas.Camera.Zoom * factor, anchor);
            _canvas.Clamp();
        }
    }
}
