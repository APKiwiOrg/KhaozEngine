using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE FORMAT AND USAGE MAP, split by domain across <c>MetalFormats.*.cs</c> partials.
    ///
    /// <para><b>THE SPLIT IS THE RATCHET'S OWN RECORDED ANSWER rather than a preference.</b> The incumbent's
    /// <c>MTLFormats.cs</c> is 700 lines against an 800-line cap, and section 18 names it as one of the two files
    /// to watch in this phase. It grows when the RENDERER gains a feature rather than when data does, which is the
    /// growth test the file-size rule states, and the answer for a file with that shape is
    /// <c>&lt;Domain&gt;</c> partials, exactly as <c>ShaderSources</c> was split in 14.8.1. This partial is the
    /// PIXEL domain: pixel formats, texture types and texture usage. Sampler state is
    /// <c>MetalFormats.State.cs</c>. Vertex formats have no consumer until the pipeline row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577) and land there as <c>MetalFormats.Vertex.cs</c>,
    /// because a table nothing calls is a table nothing tests.</para>
    ///
    /// <para><b>EVERY ROW REPRODUCES <c>Veldrid.MTL.MTLFormats</c> and the citation is the member name rather
    /// than a line number</b> (V-I6, and phase 2's cited line numbers went stale inside one release). A format map
    /// that disagreed with the incumbent's would move every pixel of every golden in the affected format at once,
    /// and unlike the staging layout it would not even be silent: the goldens are baked through the incumbent's
    /// choices.</para>
    /// </summary>
    internal static partial class MetalFormats
    {
        /// <summary>
        /// <c>MTLFormats.VdToMTLPixelFormat</c>, restricted to the eight members
        /// <see cref="GpuPixelFormat"/> has.
        ///
        /// <para><b><paramref name="depthFormat"/> IS THE WHOLE REASON THIS TAKES TWO ARGUMENTS.</b>
        /// <see cref="GpuPixelFormat.R32Float"/> is a single float channel that the 3D pass uses BOTH as a colour
        /// attachment (the linear-depth MRT target) and, on a shadow map, as a depth attachment. Metal has two
        /// different pixel formats for those and the incumbent picks between them on the texture's declared
        /// DepthStencil usage, so a texture's format is not a function of the seam's format alone. Getting it
        /// backwards produces a depth target the shadow pass cannot write, which is a black shadow map rather
        /// than an error.</para>
        /// </summary>
        /// <param name="format">The seam's pixel format.</param>
        /// <param name="depthFormat">Whether the texture declares <see cref="GpuTextureUsage.DepthStencil"/>.</param>
        internal static MTLPixelFormat ToPixelFormat(GpuPixelFormat format, bool depthFormat) => format switch
        {
            GpuPixelFormat.R8UNorm => MTLPixelFormat.R8Unorm,
            GpuPixelFormat.R32Float => depthFormat ? MTLPixelFormat.Depth32Float : MTLPixelFormat.R32Float,
            GpuPixelFormat.R16G16Float => MTLPixelFormat.RG16Float,
            GpuPixelFormat.R8G8B8A8UNorm => MTLPixelFormat.RGBA8Unorm,
            GpuPixelFormat.B8G8R8A8UNorm => MTLPixelFormat.BGRA8Unorm,
            GpuPixelFormat.R16G16B16A16Float => MTLPixelFormat.RGBA16Float,
            GpuPixelFormat.D24UNormS8UInt => MTLPixelFormat.Depth24UnormStencil8,
            GpuPixelFormat.D32FloatS8UInt => MTLPixelFormat.Depth32FloatStencil8,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "The native Metal backend has no MTLPixelFormat for that GPU seam format. The seam gained a "
                + "member this map has not been taught, and every texture in it would be created in whatever "
                + "format a guess produced."),
        };

        /// <summary>
        /// <c>FormatHelpers.IsStencilFormat</c>: whether a depth format carries a STENCIL PLANE as well as a
        /// depth one.
        /// <para>
        /// <b>IT DECIDES WHETHER A STENCIL ATTACHMENT IS NAMED AT ALL, and TWO descriptors ask it.</b> Metal
        /// splits the two planes in both places, so both call sites write the second member only for a combined
        /// format. The RENDER PASS descriptor (section 7.1) splits them across <c>depthAttachment</c> and
        /// <c>stencilAttachment</c>, where naming the second over a depth-only texture is a validation error
        /// under the debug layer M-T7 arms on every run. The RENDER PIPELINE descriptor splits them across
        /// <c>depthAttachmentPixelFormat</c> and <c>stencilAttachmentPixelFormat</c>, where naming a stencil
        /// format the framebuffer's texture does not have makes the pipeline incompatible with it, which Metal
        /// rejects at creation. The incumbent asks this same question at both points, so this is reproduction
        /// rather than a rule this backend invents.
        /// </para>
        /// <para>
        /// THE TWO COMBINED FORMATS ARE THE WHOLE ANSWER, and a colour format reaching here answers false rather
        /// than throwing: the question is asked of the DEPTH attachment's format, and neither a framebuffer with
        /// no depth attachment nor a pipeline with no depth output ever asks it.
        /// </para>
        /// </summary>
        internal static bool IsStencilFormat(GpuPixelFormat format)
            => format is GpuPixelFormat.D24UNormS8UInt or GpuPixelFormat.D32FloatS8UInt;

        /// <summary>
        /// <c>MTLFormats.VdToMTLTextureType</c>, for the one texture type the seam can express.
        /// <para>
        /// THE ORDER OF THE TESTS IS THE INCUMBENT'S AND IT MATTERS: cube wins over multisample, and multisample
        /// wins over array. A multisampled cubemap and a multisampled array are both unreachable through
        /// <see cref="GpuTextureDescription"/> (an MSAA texture must have one mip level and the engine clamps its
        /// sample count upstream), so the ordering decides nothing today and reproducing it costs nothing.
        /// </para>
        /// </summary>
        /// <param name="arrayLayers">The seam's array layer count, before any cubemap expansion.</param>
        /// <param name="multisampled">Whether the sample count is above 1.</param>
        /// <param name="cube">Whether the texture declares <see cref="GpuTextureUsage.Cubemap"/>.</param>
        internal static MTLTextureType TextureTypeFor(uint arrayLayers, bool multisampled, bool cube)
        {
            if (cube) return arrayLayers > 1 ? MTLTextureType.TypeCubeArray : MTLTextureType.TypeCube;
            if (multisampled) return MTLTextureType.Type2DMultisample;
            return arrayLayers > 1 ? MTLTextureType.Type2DArray : MTLTextureType.Type2D;
        }

        /// <summary>
        /// <c>MTLFormats.VdToMTLTextureUsage</c>, reproduced including what it does NOT map.
        ///
        /// <para><b>ONE METAL BIT SERVES TWO SEAM USAGES.</b> <see cref="GpuTextureUsage.RenderTarget"/> and
        /// <see cref="GpuTextureUsage.DepthStencil"/> both set <see cref="MTLTextureUsage.RenderTarget"/>, because
        /// Metal has one attachment bit and the aspect comes from the pixel format instead.</para>
        ///
        /// <para><b>THREE SEAM USAGES SET NO BIT AT ALL, and that is reproduction rather than an omission.</b>
        /// <see cref="GpuTextureUsage.Staging"/> is not a texture here at all (M-C5).
        /// <see cref="GpuTextureUsage.Cubemap"/> is a texture TYPE rather than a usage.
        /// <see cref="GpuTextureUsage.GenerateMipmaps"/> adds nothing, which is worth naming because it is the one
        /// a reader will doubt: <c>-generateMipmapsForTexture:</c> constrains the FORMAT (colour-renderable and
        /// filterable) rather than the usage bits, the incumbent adds no bit for it, and the committed metal
        /// goldens include mipped textures baked under exactly that. Adding <see cref="MTLTextureUsage.ShaderRead"/>
        /// here for it would be an improvement over a behaviour the goldens already prove correct.</para>
        /// </summary>
        internal static MTLTextureUsage ToTextureUsage(GpuTextureUsage usage)
        {
            MTLTextureUsage result = MTLTextureUsage.Unknown;

            if ((usage & GpuTextureUsage.Sampled) != 0) result |= MTLTextureUsage.ShaderRead;
            if ((usage & GpuTextureUsage.Storage) != 0) result |= MTLTextureUsage.ShaderWrite;
            if ((usage & (GpuTextureUsage.DepthStencil | GpuTextureUsage.RenderTarget)) != 0)
                result |= MTLTextureUsage.RenderTarget;

            return result;
        }
    }
}
