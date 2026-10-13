using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Windowing;

internal interface INativeGestureRegistration
{
    bool Install(NativeGestureRegistration owner);
    bool Remove();
    void Release();
}

/// <summary>Window-thread ownership of registration, callback roots and deferred storage release.</summary>
internal sealed class NativeGestureRegistration : IDisposable
{
    readonly nint _window;
    readonly INativeGestureRegistration? _backend;
    readonly InputAccumulator _input;
    readonly Func<bool> _acceptsInput;
    GCHandle _root;
    int _active;
    bool _installed;
    bool _detached;
    bool _released;
    bool _disposed;

    internal bool IsInstalled => _installed && !_disposed;
    internal NativeGestureFailure? Failure { get; private set; }

    internal NativeGestureRegistration(nint window, INativeGestureRegistration? backend,
        InputAccumulator input, Func<bool> acceptsInput)
    {
        _window = window;
        _backend = backend;
        _input = input;
        _acceptsInput = acceptsInput;
        input.EnablePointerGestures();
        if (backend == null) return;
        // Native pointers cannot keep the managed delegate and its storage owner alive.
        _root = GCHandle.Alloc(this);
        try
        {
            _installed = backend.Install(this);
            if (!_installed) RecordFailure(NativeGestureFailureStage.Install);
        }
        catch (Exception error) { RecordFailure(NativeGestureFailureStage.Install, error); }
        if (!_installed) Detach();
    }

    internal void Invoke(Action callback)
    {
        _active++;
        try { callback(); }
        catch (Exception error) { RecordFailure(NativeGestureFailureStage.Callback, error); }
        finally { _active--; ReleaseIfIdle(); }
    }

    internal void Deliver(nint window, PointerGestureSample sample)
    {
        if (IsInstalled && window == _window && _acceptsInput()) _input.OnPointerGesture(sample);
    }

    internal void OnSilkScroll(Vector2 delta, Vector2 positionPixels, bool commandDown)
    {
        if (_disposed) return;
        _input.OnScroll(delta);
        if (!IsInstalled && _acceptsInput())
        {
            var sample = NativeGestureNormalization.Wheel(delta, positionPixels, commandDown);
            try { _input.OnPointerGesture(sample); }
            catch (ArgumentOutOfRangeException error) { RecordFailure(NativeGestureFailureStage.Callback, error); }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
    }

    void Detach()
    {
        if (_backend == null || _released) return;
        if (!_detached)
        {
            try
            {
                _detached = _backend.Remove();
                if (!_detached) RecordFailure(NativeGestureFailureStage.Remove);
            }
            catch (Exception error) { RecordFailure(NativeGestureFailureStage.Remove, error); }
        }
        // A failed removal leaves the GC root and all native callback storage intact.
        ReleaseIfIdle();
    }

    void ReleaseIfIdle()
    {
        if (!_detached || _active != 0 || _released) return;
        try
        {
            _backend?.Release();
            _released = true;
            if (_root.IsAllocated) _root.Free();
        }
        catch (Exception error) { RecordFailure(NativeGestureFailureStage.Release, error); }
    }

    internal void RecordFailure(NativeGestureFailureStage stage, Exception? error = null)
    {
        if (Failure != null) return;
        try
        {
            Failure = new NativeGestureFailure(stage, stage switch
            {
                NativeGestureFailureStage.Install => "Native gesture registration failed.",
                NativeGestureFailureStage.Remove => "Native gesture removal failed.",
                NativeGestureFailureStage.Release => "Native gesture storage release failed.",
                _ => "Native gesture callback failed.",
            }, error);
        }
        catch { } // Diagnostic allocation failure must not escape a native callback.
    }
}
