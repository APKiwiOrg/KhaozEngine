using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLBuffer</c> handle: the one allocation primitive this backend has, because decision M-M1 says there
    /// is no allocator and no <c>MTLHeap</c> at all. <c>-newBufferWithLength:options:</c> IS the allocation.
    /// <para>
    /// EVERY BUFFER IS <see cref="MTLStorageMode.Shared"/> (M-M2), so <see cref="Contents"/> is a stable pointer
    /// for the buffer's whole life that both the CPU and the GPU address. That is what makes the uniform ring a
    /// <c>memcpy</c> rather than a staged upload, what makes a staging buffer mappable with no unmap, and what
    /// makes the software staging layout of M-C5 describe real bytes at a real address.
    /// </para>
    /// <para>
    /// IT ARRIVES AT +1 AND THE OWNER RELEASES IT. <c>-newBufferWithLength:options:</c> follows the new rule, so
    /// unlike a command buffer this is not autoreleased and the wrapper that holds it must call
    /// <see cref="Release"/> exactly once. A buffer still referenced by a submitted <c>MTLCommandBuffer</c>
    /// survives that release, because a command buffer retains everything its encoders reference until it
    /// completes (M-H3), which is what removes the retire list this backend does not have.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLBuffer(IntPtr Handle)
    {
        /// <summary>True when there is no buffer, which is what a device out of memory answers.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-contents</c>: the CPU-visible base pointer of a <see cref="MTLStorageMode.Shared"/> buffer, stable
        /// for the buffer's life.
        /// <para>
        /// NOTHING UNMAPS IT, which is why <c>IGpuDevice.Unmap</c> is a no-op on this backend exactly as it is on
        /// the incumbent. There is no map call either: the pointer simply exists.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr Contents() => ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("contents"));

        /// <summary><c>-length</c>: the allocation's real size, which is the ROUNDED size rather than the size the
        /// GPU seam asked for (see <c>MetalBuffer</c> for the rounding and why it is reproduced).</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal nuint Length() => ObjCMsgSend.SendNUInt(Handle, ObjCRuntime.Sel("length"));

        /// <summary>Release this buffer. Only ever called on a handle that arrived at +1 from
        /// <c>-newBufferWithLength:options:</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
