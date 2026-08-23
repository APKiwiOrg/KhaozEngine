using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// A <c>CAMetalDrawable</c> handle: one presentable image out of a <see cref="CAMetalLayer"/>, holding the
    /// <c>MTLTexture</c> a frame's colour attachment points at.
    ///
    /// <para><b>IT ARRIVES AUTORELEASED AND THE SWAPCHAIN RETAINS IT, which is the incumbent's own handling and
    /// the reason it is not optional.</b> A drawable is acquired at the present boundary and held across a whole
    /// frame of the consumer's recording, which is an unbounded stretch of somebody else's code with autorelease
    /// pools opening and closing inside it. That is the same lifetime argument
    /// <see cref="MetalCommandBufferSource"/> makes about <c>-commandBuffer</c>, and the same answer.</para>
    ///
    /// <para><b>ITS TEXTURE IS BORROWED FROM IT AND NEVER RELEASED SEPARATELY.</b> <c>-texture</c> is a property
    /// read, not a factory, so the handle it answers is owned by the drawable and lives exactly as long as the
    /// retained drawable does. That is what makes the swapchain framebuffer's colour attachment safe to bind for
    /// the whole recording: the acquire pins the drawable, and only the next present boundary lets it go.</para>
    ///
    /// <para><b>A NIL DRAWABLE IS A NORMAL ANSWER (M-W5).</b> <c>-nextDrawable</c> returns nil when the layer has
    /// none to give, which the incumbent turned into a whole frame of recording built and thrown away with nothing
    /// logged and nothing counted. Here it is the device-owned ORPHAN target and a skipped present, and
    /// <see cref="IsNull"/> is the one question that decides between them.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct CAMetalDrawable(IntPtr Handle)
    {
        /// <summary>True when the layer had no drawable to give, which is M-W5's whole condition.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Retain the drawable, so it survives the pool the acquire ran under.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Retain()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRetain(Handle);
        }

        /// <summary>Release the retain the acquire took. Called exactly once per acquired drawable, at the
        /// present boundary that replaces it or at teardown.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary><c>-texture</c>: the colour attachment for the frame this drawable is bound to. Borrowed from
        /// the drawable, so it is never released on its own.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLTexture Texture() => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("texture")));
    }
}
