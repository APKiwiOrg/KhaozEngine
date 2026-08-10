using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary><c>MTLSamplerMinMagFilter</c>, an <c>NSUInteger</c>. Two members and no third.</summary>
    internal enum MTLSamplerMinMagFilter : ulong
    {
        /// <summary>Nearest-neighbour, which is the seam's point filter.</summary>
        Nearest = 0,

        /// <summary>Linear, which is the seam's linear filter and also what anisotropic degrades to.</summary>
        Linear = 1,
    }

    /// <summary>
    /// <c>MTLSamplerMipFilter</c>, an <c>NSUInteger</c>. Three members, and <see cref="NotMipmapped"/> is the one
    /// this backend never selects: the incumbent maps every seam filter onto <see cref="Nearest"/> or
    /// <see cref="Linear"/>, so a sampler here always mip-filters even when the texture has one level.
    /// </summary>
    internal enum MTLSamplerMipFilter : ulong
    {
        /// <summary>Sample mip 0 only. Never selected, and present so the zero value has a name.</summary>
        NotMipmapped = 0,

        /// <summary>Pick the nearest mip level.</summary>
        Nearest = 1,

        /// <summary>Blend between two mip levels.</summary>
        Linear = 2,
    }

    /// <summary>
    /// <c>MTLSamplerAddressMode</c>, an <c>NSUInteger</c>. The full set, because the mapping in
    /// <c>MetalFormats</c> is total over <see cref="GpuSamplerAddress"/> and reads better against a complete
    /// table than against a subset a reader has to trust.
    /// </summary>
    internal enum MTLSamplerAddressMode : ulong
    {
        /// <summary>The seam's <see cref="GpuSamplerAddress.Clamp"/>.</summary>
        ClampToEdge = 0,

        /// <summary>Mirror once and then clamp. Not reachable from the seam.</summary>
        MirrorClampToEdge = 1,

        /// <summary>The seam's <see cref="GpuSamplerAddress.Wrap"/>, and what the shared sampler pair takes on all
        /// three axes.</summary>
        Repeat = 2,

        /// <summary>The seam's <see cref="GpuSamplerAddress.Mirror"/>.</summary>
        MirrorRepeat = 3,

        /// <summary>Clamp to transparent black. Not reachable from the seam.</summary>
        ClampToZero = 4,

        /// <summary>The seam's <see cref="GpuSamplerAddress.Border"/>, whose colour comes from
        /// <see cref="MTLSamplerBorderColor"/>.</summary>
        ClampToBorderColor = 5,
    }

    /// <summary>
    /// <c>MTLSamplerBorderColor</c>, an <c>NSUInteger</c>. The seam exposes no border colour at all, so the engine
    /// hardcodes <see cref="TransparentBlack"/> on every backend and this enum exists to name that one value
    /// rather than to offer a choice.
    /// </summary>
    internal enum MTLSamplerBorderColor : ulong
    {
        /// <summary>What every sampler in this engine is created with.</summary>
        TransparentBlack = 0,

        /// <summary>Opaque black. Never selected.</summary>
        OpaqueBlack = 1,

        /// <summary>Opaque white. Never selected.</summary>
        OpaqueWhite = 2,
    }

    /// <summary>
    /// An <c>MTLSamplerDescriptor</c>, created at +1 through <c>alloc</c> plus <c>init</c> and released once the
    /// sampler state exists. Write-only for the same reason <see cref="MTLTextureDescriptor"/> is.
    /// <para>
    /// <b>THE BORDER COLOUR IS SET AND THE COMPARE FUNCTION IS NOT, and that pair IS the incumbent's two
    /// conditionals resolved</b> (row 6's "both reachable conditionals"). <c>Veldrid.MTL.MTLSampler</c> writes the
    /// border colour only <c>if (gd.MetalFeatures.IsMacOS)</c>, which is true on every machine this backend runs
    /// on, so that arm is always taken. It writes the compare function only when the seam supplied a comparison
    /// kind, and the engine's own Veldrid path passes <c>null</c> at every call site, so that arm is never taken
    /// and the descriptor keeps its default of <c>MTLCompareFunctionNever</c>. Reproducing a conditional
    /// whose condition is constant would be reproducing a branch instead of a behaviour, so the constant answers
    /// are written directly and this paragraph is the citation for both. <c>MetalSamplerPolicy</c> carries the
    /// same statement where a test can read it.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLSamplerDescriptor(IntPtr Handle)
    {
        /// <summary>True when the descriptor could not be created.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh descriptor at +1, or nil.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLSamplerDescriptor New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLSamplerDescriptor");
            if (cls == IntPtr.Zero) return new MTLSamplerDescriptor(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLSamplerDescriptor(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>
        /// Write the whole request, in the incumbent's own order and with its own values. The LOD clamps are the
        /// ones the engine's Veldrid path passes (<c>0</c> and <c>uint.MaxValue</c>), and the maximum anisotropy
        /// is raised to at least 1 exactly as <c>Veldrid.MTL.MTLSampler</c> raises it, because Metal rejects 0.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Configure(MTLSamplerAddressMode s, MTLSamplerAddressMode t, MTLSamplerAddressMode r,
            MTLSamplerMinMagFilter min, MTLSamplerMinMagFilter mag, MTLSamplerMipFilter mip,
            MTLSamplerBorderColor border, nuint maxAnisotropy, float lodMinClamp, float lodMaxClamp)
        {
            SetNUInt("setSAddressMode:", (nuint)s);
            SetNUInt("setTAddressMode:", (nuint)t);
            SetNUInt("setRAddressMode:", (nuint)r);
            SetNUInt("setMinFilter:", (nuint)min);
            SetNUInt("setMagFilter:", (nuint)mag);
            SetNUInt("setMipFilter:", (nuint)mip);
            SetNUInt("setBorderColor:", (nuint)border);
            SetNUInt("setMaxAnisotropy:", maxAnisotropy);
            ObjCMsgSend.SendVoidFloat(Handle, ObjCRuntime.Sel("setLodMinClamp:"), lodMinClamp);
            ObjCMsgSend.SendVoidFloat(Handle, ObjCRuntime.Sel("setLodMaxClamp:"), lodMaxClamp);
        }

        /// <summary>Release this descriptor.</summary>
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

    /// <summary>
    /// An <c>MTLSamplerState</c> handle, the immutable object a descriptor produces. Arrives at +1 from
    /// <c>-newSamplerStateWithDescriptor:</c> and is released once by its wrapper.
    /// <para>
    /// A SEPARATE FILE WOULD BE THE FOLDER'S RULE AND IS NOT WORTH IT HERE. The rule is one file per Objective-C
    /// class so that a class's selectors and enums have one home, and this class has exactly one member: the
    /// release. Keeping it beside the descriptor that makes it is the whole of its context.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLSamplerState(IntPtr Handle)
    {
        /// <summary>True when the device would not make a sampler state.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this sampler state.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
