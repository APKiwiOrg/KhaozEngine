using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSArray</c> handle, read-only, with the two members an enumeration needs. The only array this
    /// backend receives is <c>MTLCopyAllDevices()</c>'s, which M-N1's <c>KE_METAL_DEVICE</c> selection walks.
    /// <para>
    /// THE ELEMENTS ARE BORROWED, NOT OWNED. <c>-objectAtIndex:</c> returns the array's own reference without
    /// retaining it, so an element read out of an array that is then released is a dangling pointer. Every caller
    /// here either uses the element inside the array's lifetime or retains it, and
    /// <see cref="MetalDeviceEnumeration"/> is the one that retains.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct NSArray(IntPtr Handle)
    {
        /// <summary>True when this is nil. An empty array and a nil array are different things, and only the
        /// second one means the call failed.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>How many elements the array holds, zero for nil.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal nuint Count()
            => Handle == IntPtr.Zero ? 0 : ObjCMsgSend.SendNUInt(Handle, ObjCRuntime.Sel("count"));

        /// <summary>
        /// The element at <paramref name="index"/>, BORROWED. Out of range is an Objective-C exception rather
        /// than a nil, so callers walk against <see cref="Count"/> and never guess.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr ObjectAt(nuint index)
            => ObjCMsgSend.SendPtrNUInt(Handle, ObjCRuntime.Sel("objectAtIndex:"), index);
    }
}
