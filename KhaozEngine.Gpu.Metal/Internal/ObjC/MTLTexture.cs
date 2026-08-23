using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLPixelFormat</c>, an <c>NSUInteger</c> and therefore <c>ulong</c>. THE MEMBERS THIS ENGINE CAN NAME,
    /// and deliberately not the whole enum: <see cref="GpuPixelFormat"/> has eight members, the swapchain adds the
    /// sRGB pair of one of them, and a format nothing can ask for would be a row nothing can test.
    /// <para>
    /// THE NUMBERS ARE THE HEADER'S AND NOT A SEQUENCE. Metal's values are sparse, so an implicitly numbered enum
    /// here would be silently wrong for every member, which is the failure mode a hand-rolled interop layer dies
    /// of. Each is written out and each matches the incumbent's own transcription
    /// (<c>Veldrid.MetalBindings.MTLPixelFormat</c>), which is a second independent source for the same table.
    /// </para>
    /// <para>
    /// THE DEPTH READINGS ARE WHY <c>R32Float</c> APPEARS TWICE IN THE MAP RATHER THAN TWICE HERE. The seam's
    /// <see cref="GpuPixelFormat.R32Float"/> becomes <see cref="Depth32Float"/> when the texture declares depth
    /// usage and <see cref="R32Float"/> otherwise, which is the incumbent's own conditional and lives in
    /// <c>MetalFormats</c> where it can be tested.
    /// </para>
    /// </summary>
    internal enum MTLPixelFormat : ulong
    {
        /// <summary>Not a format.</summary>
        Invalid = 0,

        /// <summary><c>MTLPixelFormatR8Unorm</c>: the seam's single-channel byte format.</summary>
        R8Unorm = 10,

        /// <summary><c>MTLPixelFormatR32Float</c>: the linear-depth MRT attachment read as COLOUR.</summary>
        R32Float = 55,

        /// <summary><c>MTLPixelFormatRG16Float</c>: the screen-space distortion offset target.</summary>
        RG16Float = 65,

        /// <summary><c>MTLPixelFormatRGBA8Unorm</c>: the 2D and 3D colour-target format.</summary>
        RGBA8Unorm = 70,

        /// <summary><c>MTLPixelFormatBGRA8Unorm</c>: what the swapchain and every golden readback use, and the
        /// format the buffer-offset alignment question is asked about in <c>MetalDeviceRequirements</c>.</summary>
        BGRA8Unorm = 80,

        /// <summary><c>MTLPixelFormatBGRA8Unorm_sRGB</c>: the swapchain's other reading of the same layout, for the
        /// sRGB request row 15 honours. Declared here rather than there because a format enum with two homes is
        /// how a second copy of one starts.</summary>
        BGRA8UnormSrgb = 81,

        /// <summary><c>MTLPixelFormatRGBA16Float</c>: the HDR internal colour target.</summary>
        RGBA16Float = 115,

        /// <summary><c>MTLPixelFormatDepth32Float</c>: what <see cref="GpuPixelFormat.R32Float"/> becomes on a
        /// texture that declares depth usage.</summary>
        Depth32Float = 252,

        /// <summary><c>MTLPixelFormatDepth24Unorm_Stencil8</c>. Not supported on Apple silicon at all, which is a
        /// fact about the DEVICE rather than about this table: the incumbent mapped the same seam format to the same
        /// value and a texture asking for it fails at creation on both.</summary>
        Depth24UnormStencil8 = 255,

        /// <summary><c>MTLPixelFormatDepth32Float_Stencil8</c>: the 3D model pass's depth-stencil.</summary>
        Depth32FloatStencil8 = 260,
    }

    /// <summary>
    /// <c>MTLTextureType</c>, an <c>NSUInteger</c>. Contiguous from zero in the header, so the values are the
    /// declaration order, and the members are still written in full rather than left implicit so a later insertion
    /// cannot renumber the table silently.
    /// <para>
    /// THE 1D AND 3D MEMBERS ARE ABSENT BECAUSE THE SEAM HAS NO WAY TO ASK FOR THEM.
    /// <see cref="GpuTextureDescription"/> expresses 2D textures, 2D arrays and cubemaps and nothing else, and
    /// <c>MetalFormats.TextureTypeFor</c> covers exactly that.
    /// </para>
    /// </summary>
    internal enum MTLTextureType : ulong
    {
        /// <summary><c>MTLTextureType2D</c>.</summary>
        Type2D = 2,

        /// <summary><c>MTLTextureType2DArray</c>.</summary>
        Type2DArray = 3,

        /// <summary><c>MTLTextureType2DMultisample</c>, which an MSAA render target takes and which cannot also be
        /// an array through this seam.</summary>
        Type2DMultisample = 4,

        /// <summary><c>MTLTextureTypeCube</c>.</summary>
        TypeCube = 5,

        /// <summary><c>MTLTextureTypeCubeArray</c>.</summary>
        TypeCubeArray = 6,
    }

    /// <summary>
    /// <c>MTLTextureUsage</c>, an <c>NSUInteger</c> option set. What a texture declares it may be used for at
    /// CREATION, which Metal then enforces.
    /// <para>
    /// <see cref="Unknown"/> IS NOT "ANY" AND THE NAME INVITES READING IT THAT WAY. It is zero, meaning the
    /// texture declares no use at all, and it is the value <c>MTLFormats.VdToMTLTextureUsage</c> starts its
    /// accumulation from rather than a value anything ships with.
    /// </para>
    /// </summary>
    [Flags]
    internal enum MTLTextureUsage : ulong
    {
        /// <summary>No declared use. The accumulator's starting value, never a created texture's.</summary>
        Unknown = 0,

        /// <summary><c>MTLTextureUsageShaderRead</c>: sampled from a shader.</summary>
        ShaderRead = 1 << 0,

        /// <summary><c>MTLTextureUsageShaderWrite</c>: written from a compute shader, which is the seam's
        /// <see cref="GpuTextureUsage.Storage"/>.</summary>
        ShaderWrite = 1 << 1,

        /// <summary><c>MTLTextureUsageRenderTarget</c>: a colour OR depth attachment. Metal has one bit where the
        /// seam has two usages, which is why the incumbent's map ORs the same bit in from either.</summary>
        RenderTarget = 1 << 2,

        /// <summary><c>MTLTextureUsagePixelFormatView</c>: a view may reinterpret the format. Declared because the
        /// eager-view rule of M-M10 is the one place this backend creates a view at all, and a view that narrows
        /// nothing does not need it.</summary>
        PixelFormatView = 0x10,
    }

    /// <summary>
    /// An <c>MTLTexture</c> handle.
    /// <para>
    /// EVERY TEXTURE THIS BACKEND CREATES IS <see cref="MTLStorageMode.Private"/> (M-M2), reproducing the
    /// incumbent, so there is no <c>contents()</c> here and no CPU pointer of any kind. A staging texture is not
    /// one of these at all: it is a <see cref="MTLStorageMode.Shared"/> <see cref="MTLBuffer"/> with the software
    /// subresource layout of M-C5, which is the highest-risk parity surface in the backend and lives in
    /// <c>MetalStagingLayout</c>.
    /// </para>
    /// <para>
    /// IT ARRIVES AT +1 from <c>-newTextureWithDescriptor:</c> and the owner releases it exactly once.
    /// </para>
    /// <para>
    /// <b>THERE IS NO <c>-newTextureViewWithPixelFormat:textureType:levels:slices:</c> HERE, AND ITS ABSENCE IS
    /// DECISION M-M10 IN ITS STRONGEST FORM.</b> The design asks that no view factory be reachable from the
    /// recording type, so that a draw-time view is a compile error. On this backend nothing can reach one because
    /// the package declares none at all: the GPU seam has no texture-view type, so a bind names an
    /// <see cref="IGpuTexture"/> and can never narrow it by mip, layer or format, which is precisely the
    /// condition under which <c>Veldrid.MTL.MTLTextureView</c> takes its <c>else</c> branch and uses the target's
    /// own <c>DeviceTexture</c>. The incumbent still allocated a MANAGED wrapper for that on the draw path, lazily
    /// and per bind (<c>Util.GetTextureView</c> from <c>MTLCommandList</c>'s bind path), which is the shape
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/423 recorded 25 <c>DEVICE_REMOVED</c> stacks inside. Here
    /// the bindable handle IS the texture, decided at creation. <c>MetalViewPolicy</c> is where that is decided
    /// and <c>MetalEagerViewArchitectureTests</c> is what stops the selector coming back without the argument
    /// being re-made.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLTexture(IntPtr Handle)
    {
        /// <summary>True when there is no texture, which is what a device answers for a descriptor it cannot
        /// satisfy.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this texture. Only ever called on a handle that arrived at +1.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
