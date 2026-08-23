using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLTextureDescriptor</c>, the mutable request <c>-newTextureWithDescriptor:</c> reads. Created at +1
    /// through <c>alloc</c> plus <c>init</c> and released by the caller once the texture exists, which is the
    /// incumbent's own lifetime for it.
    /// <para>
    /// A DESCRIPTOR IS WRITE-ONLY HERE. Every property Metal declares has a getter and this type declares none of
    /// them: nothing in this backend reads a descriptor back, the created texture is the answer, and a getter
    /// would be a selector nothing calls and nothing tests. The setters are the ones
    /// <see cref="Configure"/> uses and no others, for the same reason.
    /// </para>
    /// <para>
    /// <c>depth</c> IS NEVER SET AND THAT IS DELIBERATE. It defaults to 1 and <see cref="GpuTextureDescription"/>
    /// has no depth parameter at all, so setting it would be writing a constant the seam cannot vary. The
    /// staging-layout arithmetic makes the same statement in the same words (<c>MetalStagingLayout</c>), and the
    /// two agreeing is what keeps a 3D texture from being half-expressible.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLTextureDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor could not be created, which means the class is missing and the
        /// process is on a machine with no Metal at all.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh descriptor at +1, or nil. The caller releases it after the texture is created,
        /// exactly as the incumbent did.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLTextureDescriptor New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLTextureDescriptor");
            if (cls == IntPtr.Zero) return new MTLTextureDescriptor(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLTextureDescriptor(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>
        /// Write the whole request in one call, because a descriptor set field by field from a caller is a
        /// descriptor a caller can leave half-written. Every property this backend ever sets is a parameter here,
        /// so <c>MetalTexture</c> reads as one statement and there is one place to compare against
        /// <c>Veldrid.MTL.MTLTexture</c>'s constructor.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(MTLTextureType type, MTLPixelFormat format, nuint width, nuint height,
            nuint mipLevels, nuint arrayLength, nuint sampleCount, MTLTextureUsage usage, MTLStorageMode storage)
        {
            SetNUInt("setTextureType:", (nuint)type);
            SetNUInt("setPixelFormat:", (nuint)format);
            SetNUInt("setWidth:", width);
            SetNUInt("setHeight:", height);
            SetNUInt("setMipmapLevelCount:", mipLevels);
            SetNUInt("setArrayLength:", arrayLength);
            SetNUInt("setSampleCount:", sampleCount);
            SetNUInt("setUsage:", (nuint)usage);
            SetNUInt("setStorageMode:", (nuint)storage);
        }

        /// <summary>Release this descriptor. Called once the texture exists, whether or not it exists.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SetNUInt(string selector, nuint value)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel(selector), value);
    }
}
