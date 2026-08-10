using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Drives an Xcode Metal GPU frame capture (<c>MTLCaptureManager</c>) around a whole frame, via the
    /// Objective-C runtime. Used only when a capture is armed (see <see cref="GpuFrameCapture"/>). Every step is
    /// best-effort and swallows errors, because a failed capture must never break rendering.
    ///
    /// <para><b>IT TAKES THE COMMAND QUEUE AS A POINTER, WHICH IS DECISION M-G5.</b> This used to reach into
    /// Veldrid's private <c>_commandQueue</c> field by reflection and return zero if the layout differed, which
    /// meant the whole feature was one Veldrid refactor away from silently producing no trace. The native Metal
    /// backend OWNS its queue, so it hands the pointer in and no reflection happens on that path at all. The
    /// Veldrid Metal path still has to find its queue somehow and the reflection survives for it, isolated in
    /// <see cref="VeldridMetalCommandQueue"/> so it is one named thing that can be tested rather than a step
    /// buried in the middle of a capture.</para>
    ///
    /// <para><b><c>MTL_CAPTURE_ENABLED=1</c> MUST BE IN THE ENVIRONMENT BEFORE THE PROCESS LAUNCHES</b>, which is
    /// the same process-launch rule M-G3 measured for the validation variables: setting it in-process does not
    /// reach the framework. Without it Metal refuses programmatic capture, and <see cref="Start"/> answers false
    /// having done nothing.</para>
    ///
    /// <para><b>THE DESTINATION IS ASKED ABOUT BEFORE THE CAPTURE IS STARTED, AND THAT GUARD IS LOAD-BEARING
    /// RATHER THAN DEFENSIVE.</b> <c>-startCaptureWithDescriptor:error:</c> on a process where capture was never
    /// enabled is documented to raise an Objective-C exception rather than to answer false through its error
    /// parameter, and an Objective-C exception crossing a managed frame is a process abort that no
    /// <c>try</c> here can catch. <c>-supportsDestination:</c> is the documented way to ask first, and it was
    /// MEASURED on an Apple M2 Max under macOS 26 to answer NO for both destinations in a process without the
    /// variable. So the guard is what makes the whole path safe to execute on an ordinary run, which is in turn
    /// what makes it testable at all.</para>
    /// </summary>
    internal static class MetalFrameCapture
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";

        /// <summary><c>MTLCaptureDestinationGPUTraceDocument</c>, the destination that writes a
        /// <c>.gputrace</c> bundle to a file rather than handing the capture to an attached Xcode.</summary>
        internal const nint GpuTraceDocument = 2;

        [DllImport(Objc, EntryPoint = "objc_getClass")] static extern IntPtr GetClass(string name);
        [DllImport(Objc, EntryPoint = "sel_registerName")] static extern IntPtr Sel(string name);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg0);
        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SendBoolNInt(IntPtr receiver, IntPtr sel, nint arg0);
        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SendStartCapture(IntPtr receiver, IntPtr sel, IntPtr descriptor, ref IntPtr error);

        [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPush")] static extern IntPtr PoolPush();
        [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPop")] static extern void PoolPop(IntPtr pool);

        static bool _capturing;

        static IntPtr NSString(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(utf8.Length);
            try { Marshal.Copy(utf8, 0, p, utf8.Length); return Send(GetClass("NSString"), Sel("stringWithUTF8String:"), p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>
        /// Whether this process can write a <c>.gputrace</c> at all, which is <c>MTL_CAPTURE_ENABLED=1</c> having
        /// been set before launch. False everywhere else, including on a non-Metal platform where the class does
        /// not exist. Split out from <see cref="Start"/> so a caller can report the reason nothing was captured,
        /// and so the guard itself is assertable.
        /// <para>
        /// IT PUSHES NO POOL OF ITS OWN, and that is a statement rather than an omission: it creates no
        /// Objective-C object at all. <c>objc_getClass</c> and <c>sel_registerName</c> allocate nothing, and
        /// <c>+sharedCaptureManager</c> is a retained singleton rather than an autoreleased instance. The two
        /// members that DO create objects push one.
        /// </para>
        /// </summary>
        internal static bool CaptureIsEnabledForThisProcess()
        {
            try
            {
                IntPtr cls = GetClass("MTLCaptureManager");
                if (cls == IntPtr.Zero) return false;

                IntPtr manager = Send(cls, Sel("sharedCaptureManager"));
                if (manager == IntPtr.Zero) return false;

                return SendBoolNInt(manager, Sel("supportsDestination:"), GpuTraceDocument);
            }
            catch { return false; }
        }

        /// <summary>
        /// Begin capturing everything committed to <paramref name="commandQueue"/> into a <c>.gputrace</c> at
        /// <paramref name="outputPath"/>. False when nothing was started, which is the ordinary answer on a
        /// process without <c>MTL_CAPTURE_ENABLED=1</c> and on every non-macOS platform.
        /// </summary>
        /// <param name="commandQueue">The <c>id&lt;MTLCommandQueue&gt;</c> to capture. The native backend passes
        /// its own, the Veldrid path passes whatever <see cref="VeldridMetalCommandQueue.TryRead"/> found.
        /// <see cref="IntPtr.Zero"/> answers false, which is how a Veldrid layout change presents.</param>
        /// <param name="outputPath">A fresh, non-existent path. Metal creates the <c>.gputrace</c> bundle.</param>
        internal static bool Start(IntPtr commandQueue, string outputPath)
        {
            _capturing = false;

            // THE POOL IS PUSHED HERE RATHER THAN LEFT TO THE CALLER'S, which is M-N5's rule honoured on the one
            // path the package's own architecture walk cannot see: this type lives in KhaozEngine.Gpu and keeps
            // its own objc_msgSend declarations, so the walk over KhaozEngine.Gpu.Metal never reaches it. The
            // NSString, the NSURL and the shared manager are all autoreleased, and a present boundary on a thread
            // with no pool would leak them for the life of the process.
            IntPtr pool = PoolPush();
            try
            {
                if (commandQueue == IntPtr.Zero) return false;

                // BEFORE ANY OTHER CALL. See the class remarks: starting a capture in a process where capture was
                // never enabled raises an Objective-C exception, which is a process abort rather than something
                // the catch below could turn into a false.
                if (!CaptureIsEnabledForThisProcess()) return false;

                IntPtr mgrCls = GetClass("MTLCaptureManager");
                IntPtr descCls = GetClass("MTLCaptureDescriptor");
                IntPtr nsUrlCls = GetClass("NSURL");
                if (mgrCls == IntPtr.Zero || descCls == IntPtr.Zero || nsUrlCls == IntPtr.Zero) return false;

                IntPtr mgr = Send(mgrCls, Sel("sharedCaptureManager"));
                IntPtr desc = Send(Send(descCls, Sel("alloc")), Sel("init"));

                try
                {
                    Send(desc, Sel("setCaptureObject:"), commandQueue);      // capture this command queue
                    Send(desc, Sel("setDestination:"), (IntPtr)GpuTraceDocument);
                    IntPtr url = Send(nsUrlCls, Sel("fileURLWithPath:"), NSString(outputPath));
                    Send(desc, Sel("setOutputURL:"), url);

                    IntPtr error = IntPtr.Zero;
                    _capturing = SendStartCapture(mgr, Sel("startCaptureWithDescriptor:error:"), desc, ref error);
                    return _capturing;
                }
                finally
                {
                    // -alloc/-init is +1 and the pool above does not cover it, so the descriptor is released
                    // explicitly. The capture manager takes what it needs out of it at the start call, so this is
                    // safe on both the started and the refused path.
                    Send(desc, Sel("release"));
                }
            }
            catch { return false; }
            finally { PoolPop(pool); }
        }

        /// <summary>
        /// End the capture started by <see cref="Start"/>, after letting the captured frame's GPU work finish. A
        /// no-op when nothing is capturing, which is what makes an unconditional call at a present boundary safe.
        /// </summary>
        /// <param name="waitForIdle">The owning device's own drain. Called only when a capture is actually in
        /// progress, so an ordinary frame pays nothing for it. Closing the trace without it would end the
        /// document with the frame's work still running.</param>
        internal static void Stop(Action waitForIdle)
        {
            if (!_capturing) return;

            IntPtr pool = PoolPush();
            try
            {
                waitForIdle();
                IntPtr mgr = Send(GetClass("MTLCaptureManager"), Sel("sharedCaptureManager"));
                Send(mgr, Sel("stopCapture"));
            }
            catch { /* best-effort */ }
            finally
            {
                PoolPop(pool);
                _capturing = false;
            }
        }
    }
}
