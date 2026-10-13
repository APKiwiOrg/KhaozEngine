using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Windowing;

public sealed partial class AppWindow
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate nint WindowsGestureCallback(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint refData);

    [StructLayout(LayoutKind.Sequential)]
    struct WindowsGesturePoint { public int X, Y; }

    sealed class WindowsGestureRegistration(AppWindow app, nint window) : INativeGestureRegistration
    {
        WindowsGestureCallback? _callback;
        nint _callbackAddress;
        nuint _subclassId;
        bool _registered;

        public bool Install(NativeGestureRegistration owner)
        {
            _callback = (hwnd, message, wParam, lParam, _, _) =>
            {
                var callbackRoot = _callback;
                nint result = 0;
                try
                {
                    owner.Invoke(() =>
                    {
                        try
                        {
                            if (owner.IsInstalled && hwnd == window && (message == 0x020A || message == 0x020E))
                            {
                                var screen = NativeGestureNormalization.WindowsScreenPosition(lParam);
                                var point = new WindowsGesturePoint { X = (int)screen.X, Y = (int)screen.Y };
                                if (WindowsGestureScreenToClient(hwnd, ref point)
                                    && NativeGestureNormalization.TryWindowsWheel(message, wParam,
                                        new Vector2(point.X, point.Y), app.FramebufferScale(), out var sample))
                                    owner.Deliver(hwnd, sample);
                            }
                        }
                        finally
                        {
                            // Remove only our callback and ID, including a native window teardown.
                            if (message == 0x0082 && hwnd == window) owner.Dispose();
                            result = WindowsGestureDefSubclassProc(hwnd, message, wParam, lParam);
                        }
                    });
                }
                catch (Exception error) { owner.RecordFailure(NativeGestureFailureStage.Callback, error); }
                GC.KeepAlive(callbackRoot);
                return result;
            };
            _callbackAddress = Marshal.GetFunctionPointerForDelegate(_callback);
            _subclassId = unchecked((nuint)_callbackAddress);
            _registered = WindowsGestureSetSubclass(window, _callbackAddress, _subclassId, 0);
            return _registered;
        }

        public bool Remove()
        {
            if (!_registered) return true;
            if (!WindowsGestureRemoveSubclass(window, _callbackAddress, _subclassId)) return false;
            _registered = false;
            return true;
        }

        public void Release() { _callback = null; _callbackAddress = 0; }
    }

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsGestureSetSubclass(nint window, nint callback, nuint subclassId, nuint refData);
    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsGestureRemoveSubclass(nint window, nint callback, nuint subclassId);
    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc", ExactSpelling = true)]
    static extern nint WindowsGestureDefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "ScreenToClient", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsGestureScreenToClient(nint window, ref WindowsGesturePoint point);
}
