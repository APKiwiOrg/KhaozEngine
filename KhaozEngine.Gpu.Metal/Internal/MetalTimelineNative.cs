using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE BLOCK LAYOUT AND ONE STRING READ, which is all that remains of row 5's pre-merge interop shim. The
    /// duplicated message-send and runtime declarations were absorbed into <c>Internal/ObjC/</c> at the rows'
    /// merge per the #570 handoff, and every consumer reads through <see cref="ObjCMsgSend"/> and
    /// <see cref="ObjCRuntime"/> now. What stays here has no per-class home yet: the Objective-C block ABI
    /// structs the completion handler builds its global block from, and the NSString-to-managed read the fault
    /// path uses. The recording rows give blocks a proper <c>ObjC/</c> file if a second block user appears.
    /// </summary>
    internal static unsafe class MetalTimelineNative
    {
        /// <summary>
        /// An Objective-C block, which is what <c>addCompletedHandler:</c> takes. The layout is the ABI's rather
        /// than ours: isa, flags, reserved, invoke, descriptor. M-F3 puts an <c>[UnmanagedCallersOnly]</c> static
        /// in the invoke slot, so there is no delegate, no <c>Marshal.GetFunctionPointerForDelegate</c> and no GC
        /// handle anywhere on the completion path.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockLiteral
        {
            internal IntPtr Isa;
            internal int Flags;
            internal int Reserved;
            internal IntPtr Invoke;
            internal IntPtr Descriptor;
        }

        /// <summary>A block's descriptor. A block with no captures needs no copy helper and no dispose helper,
        /// so the two-field form is the whole of it.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockDescriptor
        {
            internal nuint Reserved;
            internal nuint Size;
        }

        /// <summary>An <c>NSString</c> as a managed string, empty for nil.</summary>
        [SupportedOSPlatform("macos")]
        internal static string NSStringToManaged(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return "";
            IntPtr utf8 = ObjCMsgSend.Send(nsString, ObjCRuntime.Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }
    }
}
