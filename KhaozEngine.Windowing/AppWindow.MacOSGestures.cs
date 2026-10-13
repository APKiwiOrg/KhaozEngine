using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Windowing;

public sealed partial class AppWindow
{
    const string MacObjCLibrary = "/usr/lib/libobjc.A.dylib";
    const string MacBlockLibrary = "/usr/lib/libSystem.B.dylib";
    static nint s_macBlockLibrary;
    static nint s_macBlockDescriptor;

    [StructLayout(LayoutKind.Sequential)]
    struct MacPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MacBlockLiteral
    {
        public nint Isa;
        public int Flags, Reserved;
        public nint Invoke, Descriptor;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint MacMonitorCallback(nint block, nint inputEvent);

    sealed class MacOSGestureRegistration(AppWindow app, nint window) : INativeGestureRegistration
    {
        nint _monitor, _block, _eventClass;
        MacMonitorCallback? _callback;
        MacGestureSelectors _selectors;

        public unsafe bool Install(NativeGestureRegistration owner)
        {
            _selectors = new MacGestureSelectors();
            _eventClass = MacGetClass("NSEvent");
            if (_eventClass == 0) return false;
            if (s_macBlockLibrary == 0) s_macBlockLibrary = NativeLibrary.Load(MacBlockLibrary);
            if (s_macBlockDescriptor == 0)
            {
                // Like a compiled block descriptor, this capture-free descriptor has process lifetime.
                s_macBlockDescriptor = Marshal.AllocHGlobal(2 * nint.Size);
                Marshal.WriteIntPtr(s_macBlockDescriptor, 0, 0);
                Marshal.WriteIntPtr(s_macBlockDescriptor, nint.Size, Marshal.SizeOf<MacBlockLiteral>());
            }
            _callback = (_, inputEvent) =>
            {
                var callbackRoot = _callback;
                try
                {
                    owner.Invoke(() =>
                    {
                        if (!owner.IsInstalled || MacSendPointer(inputEvent, _selectors.Window) != window) return;
                        int type = (int)MacSendUnsigned(inputEvent, _selectors.Type);
                        var position = MacSendPoint(inputEvent, _selectors.Location);
                        bool scroll = type == 22;
                        var input = new NativeMacOSGestureEvent(type, position.X, position.Y,
                            scroll ? MacSendDouble(inputEvent, _selectors.DeltaX) : 0,
                            scroll ? MacSendDouble(inputEvent, _selectors.DeltaY) : 0,
                            scroll && MacSendBool(inputEvent, _selectors.Precise) != 0,
                            scroll ? 0 : MacSendDouble(inputEvent, _selectors.Magnification),
                            (ulong)MacSendUnsigned(inputEvent, _selectors.Modifiers),
                            (ulong)MacSendUnsigned(inputEvent, _selectors.Phase),
                            scroll ? (ulong)MacSendUnsigned(inputEvent, _selectors.Momentum) : 0);
                        if (NativeGestureNormalization.TryMacOS(input, app._window.Size.Y, app.FramebufferScale(), out var sample))
                            owner.Deliver(window, sample);
                    });
                }
                catch (Exception error) { owner.RecordFailure(NativeGestureFailureStage.Callback, error); }
                GC.KeepAlive(callbackRoot);
                return inputEvent; // GLFW must still dispatch the original event.
            };
            var literal = new MacBlockLiteral
            {
                Isa = NativeLibrary.GetExport(s_macBlockLibrary, "_NSConcreteStackBlock"),
                Invoke = Marshal.GetFunctionPointerForDelegate(_callback),
                Descriptor = s_macBlockDescriptor,
            };
            // Copy a stack literal into runtime-owned heap storage. AppKit may copy it again.
            _block = MacBlockCopy((nint)(&literal));
            if (_block == 0) return false;
            _monitor = MacAddMonitor(_eventClass, _selectors.AddMonitor, (1UL << 22) | (1UL << 30), _block);
            return _monitor != 0;
        }

        public bool Remove()
        {
            if (_monitor == 0) return true;
            MacRemoveMonitor(_eventClass, _selectors.RemoveMonitor, _monitor);
            _monitor = 0;
            return true;
        }

        public void Release()
        {
            if (_block != 0) { MacBlockRelease(_block); _block = 0; }
            _callback = null;
        }
    }

    readonly struct MacGestureSelectors
    {
        internal readonly nint Window = MacSelector("window");
        internal readonly nint Type = MacSelector("type");
        internal readonly nint Location = MacSelector("locationInWindow");
        internal readonly nint DeltaX = MacSelector("scrollingDeltaX");
        internal readonly nint DeltaY = MacSelector("scrollingDeltaY");
        internal readonly nint Precise = MacSelector("hasPreciseScrollingDeltas");
        internal readonly nint Magnification = MacSelector("magnification");
        internal readonly nint Modifiers = MacSelector("modifierFlags");
        internal readonly nint Phase = MacSelector("phase");
        internal readonly nint Momentum = MacSelector("momentumPhase");
        internal readonly nint AddMonitor = MacSelector("addLocalMonitorForEventsMatchingMask:handler:");
        internal readonly nint RemoveMonitor = MacSelector("removeMonitor:");
        public MacGestureSelectors() { }
    }

    [DllImport(MacObjCLibrary, EntryPoint = "objc_getClass", CallingConvention = CallingConvention.Cdecl)]
    static extern nint MacGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(MacObjCLibrary, EntryPoint = "sel_registerName", CallingConvention = CallingConvention.Cdecl)]
    static extern nint MacSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern nint MacSendPointer(nint receiver, nint selector);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern nuint MacSendUnsigned(nint receiver, nint selector);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern byte MacSendBool(nint receiver, nint selector);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern double MacSendDouble(nint receiver, nint selector);
    // NSPoint is two doubles returned in registers on macOS LP64 x64 and arm64.
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern MacPoint MacSendPoint(nint receiver, nint selector);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern nint MacAddMonitor(nint receiver, nint selector, ulong mask, nint block);
    [DllImport(MacObjCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    static extern void MacRemoveMonitor(nint receiver, nint selector, nint monitor);
    [DllImport(MacBlockLibrary, EntryPoint = "_Block_copy", CallingConvention = CallingConvention.Cdecl)]
    static extern nint MacBlockCopy(nint block);
    [DllImport(MacBlockLibrary, EntryPoint = "_Block_release", CallingConvention = CallingConvention.Cdecl)]
    static extern void MacBlockRelease(nint block);
}
