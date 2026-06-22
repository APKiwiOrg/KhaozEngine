using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Drives an Xcode Metal GPU frame capture (<c>MTLCaptureManager</c>) around a single Submit, via the
    /// Objective-C runtime. Used only when a capture is armed (see <see cref="GpuFrameCapture"/>). Captures the
    /// Veldrid command queue (obtained by reflection - Veldrid does not expose it). Every step is best-effort and
    /// swallows errors: a failed capture must never break rendering. Requires <c>MTL_CAPTURE_ENABLED=1</c> in the
    /// environment before the device was created.</summary>
    internal static class MetalFrameCapture
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";

        [DllImport(Objc, EntryPoint = "objc_getClass")] static extern IntPtr GetClass(string name);
        [DllImport(Objc, EntryPoint = "sel_registerName")] static extern IntPtr Sel(string name);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg0);
        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SendStartCapture(IntPtr receiver, IntPtr sel, IntPtr descriptor, ref IntPtr error);

        static bool _capturing;

        static IntPtr NSString(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(utf8.Length);
            try { Marshal.Copy(utf8, 0, p, utf8.Length); return Send(GetClass("NSString"), Sel("stringWithUTF8String:"), p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Veldrid's MTLGraphicsDevice keeps the command queue in a private field; reach it (and its native
        // id<MTLCommandQueue> pointer) by reflection. Returns IntPtr.Zero if the layout differs (then capture is skipped).
        static IntPtr TryGetNativeCommandQueue(GraphicsDevice gd)
        {
            var f = gd.GetType().GetField("_commandQueue", BindingFlags.NonPublic | BindingFlags.Instance);
            object? cq = f?.GetValue(gd);
            if (cq == null) return IntPtr.Zero;
            // Veldrid MTLCommandQueue is a struct wrapping `public readonly IntPtr NativePtr;`.
            var np = cq.GetType().GetField("NativePtr");
            if (np == null) return IntPtr.Zero;
            object? v = np.GetValue(cq);
            return v is IntPtr ptr ? ptr : IntPtr.Zero;
        }

        public static bool Start(GraphicsDevice gd, string outputPath)
        {
            _capturing = false;
            try
            {
                IntPtr queue = TryGetNativeCommandQueue(gd);
                if (queue == IntPtr.Zero) return false;
                IntPtr mgrCls = GetClass("MTLCaptureManager");
                IntPtr descCls = GetClass("MTLCaptureDescriptor");
                IntPtr nsUrlCls = GetClass("NSURL");
                if (mgrCls == IntPtr.Zero || descCls == IntPtr.Zero || nsUrlCls == IntPtr.Zero) return false;

                IntPtr mgr = Send(mgrCls, Sel("sharedCaptureManager"));
                IntPtr desc = Send(Send(descCls, Sel("alloc")), Sel("init"));
                Send(desc, Sel("setCaptureObject:"), queue);             // capture this command queue
                Send(desc, Sel("setDestination:"), (IntPtr)2);           // MTLCaptureDestinationGPUTraceDocument = 2
                IntPtr url = Send(nsUrlCls, Sel("fileURLWithPath:"), NSString(outputPath));
                Send(desc, Sel("setOutputURL:"), url);

                IntPtr error = IntPtr.Zero;
                _capturing = SendStartCapture(mgr, Sel("startCaptureWithDescriptor:error:"), desc, ref error);
                return _capturing;
            }
            catch { return false; }
        }

        public static void Stop(GraphicsDevice gd)
        {
            if (!_capturing) return;
            try
            {
                gd.WaitForIdle();   // let the captured frame's GPU work finish before closing the trace
                IntPtr mgr = Send(GetClass("MTLCaptureManager"), Sel("sharedCaptureManager"));
                Send(mgr, Sel("stopCapture"));
            }
            catch { /* best-effort */ }
            finally { _capturing = false; }
        }
    }
}
