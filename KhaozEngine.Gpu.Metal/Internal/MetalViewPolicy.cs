using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHAT A TEXTURE IS, DECIDED AT CREATION AND NEVER AT A BIND. Decision M-M10, plus M-M2's storage mode and
    /// the incumbent's own usage map.
    /// </summary>
    /// <param name="Staging">A <see cref="GpuTextureUsage.Staging"/> texture: a <see cref="MTLStorageMode.Shared"/>
    /// <c>MTLBuffer</c> with the software subresource layout of M-C5, no <c>MTLTexture</c> at all.</param>
    /// <param name="Type">The <c>MTLTextureType</c> the descriptor takes.</param>
    /// <param name="Usage">The <c>MTLTextureUsage</c> bits the descriptor takes.</param>
    /// <param name="Storage">The storage mode, which is <see cref="MTLStorageMode.Private"/> for every real
    /// texture (M-M2) and unused for a staging one.</param>
    /// <param name="DepthStencil">Whether the texture declares depth usage, which is what picks between the two
    /// Metal pixel formats <see cref="GpuPixelFormat.R32Float"/> can become.</param>
    /// <param name="ViewCount">How many <c>MTLTexture</c> VIEW objects creation makes. Always zero, and it is a
    /// field rather than a constant so a test can assert it over every usage the seam can express rather than
    /// assert a literal against itself.</param>
    internal readonly record struct MetalTextureViewPlan(
        bool Staging,
        MTLTextureType Type,
        MTLTextureUsage Usage,
        MTLStorageMode Storage,
        bool DepthStencil,
        int ViewCount);

    /// <summary>
    /// THE DERIVATION, and nothing else: the seam's usage bits and shape in, the creation plan out. Decision
    /// M-M10, section 9.3.
    ///
    /// <para><b>THE EAGER VIEW SET IS EMPTY, AND THAT IS THE ANSWER RATHER THAN A GAP.</b> M-M10 says every view a
    /// resource set can name is created at resource creation, following the incumbent's rule that a view object
    /// exists only when the description NARROWS the target. On this seam nothing can narrow one:
    /// <see cref="IGpuResourceFactory"/> has no texture-view type, a resource set binds an
    /// <see cref="IGpuTexture"/> whole, <c>CreateFramebuffer</c> carries no mip or layer parameter and per-face
    /// cubemap rendering is not expressible. So every case falls in <c>Veldrid.MTL.MTLTextureView</c>'s
    /// <c>else</c> branch, which uses the target's own <c>DeviceTexture</c> and creates no native object, and this
    /// backend simply binds the texture. Widening any of those is a SEAM change, and a seam change is where the
    /// view would be added, with this paragraph as the thing to re-argue.</para>
    ///
    /// <para><b>WHAT THE INCUMBENT PAYS ON THE DRAW PATH IS THE MANAGED WRAPPER, and that is what M-M10 removes
    /// here.</b> <c>Util.GetTextureView</c> is called from <c>MTLCommandList</c>'s bind path and allocates a
    /// <c>TextureView</c> under a lock the first time each texture is bound. All 25 <c>DEVICE_REMOVED</c> stacks
    /// in https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside that lazy constructor, which is why
    /// the rule is structural rather than a counter: the package declares no view factory at all, so a draw-time
    /// view is not merely a compile error but unwritable.</para>
    ///
    /// <para><b>THE USAGE MAP AND THE STORAGE MODE ARE THE INCUMBENT'S, VERBATIM.</b> Every non-staging texture is
    /// <see cref="MTLStorageMode.Private"/> and the usage bits come from <see cref="MetalFormats.ToTextureUsage"/>,
    /// which reproduces <c>MTLFormats.VdToMTLTextureUsage</c> including the three seam usages it maps to no bit at
    /// all.</para>
    /// </summary>
    internal static class MetalViewPolicy
    {
        /// <summary>
        /// The creation plan for a texture of <paramref name="usage"/> with <paramref name="arrayLayers"/> layers
        /// at <paramref name="sampleCount"/> samples.
        /// </summary>
        /// <exception cref="ArgumentException"><see cref="GpuTextureUsage.Staging"/> combined with anything else. A
        /// staging texture here is an <c>MTLBuffer</c> with a software subresource layout rather than a texture
        /// (M-C5), so there is no texture for the other bits to describe, and every staging texture the engine
        /// creates passes the bit alone.</exception>
        /// <param name="usage">The seam's usage bits.</param>
        /// <param name="arrayLayers">The seam's array layer count.</param>
        /// <param name="sampleCount">The seam's sample count.</param>
        /// <param name="arrayView"><see cref="GpuTextureDescription.IsArray"/>, which is what makes a ONE-layer
        /// array a <c>Type2DArray</c> rather than a <c>Type2D</c> (#666). Defaulted so a caller that only has a
        /// layer count keeps the derived behaviour.</param>
        internal static MetalTextureViewPlan ForTexture(GpuTextureUsage usage, uint arrayLayers, uint sampleCount,
            bool arrayView = false)
        {
            bool staging = (usage & GpuTextureUsage.Staging) != 0;
            if (staging && usage != GpuTextureUsage.Staging)
            {
                throw new ArgumentException(
                    "A staging texture is CPU-mapped and cannot be bound, so GpuTextureUsage.Staging cannot be "
                    + "combined with any other usage. On the native Metal backend a staging texture is a Shared "
                    + "MTLBuffer carrying the incumbent's software subresource layout rather than a texture "
                    + "(M-C5), so there is no MTLTexture for the other bits to describe at all. Read back by "
                    + "copying into a staging texture of its own.", nameof(usage));
            }

            if (staging)
            {
                return new MetalTextureViewPlan(true, MTLTextureType.Type2D, MTLTextureUsage.Unknown,
                    MTLStorageMode.Shared, DepthStencil: false, ViewCount: 0);
            }

            return new MetalTextureViewPlan(
                Staging: false,
                MetalFormats.TextureTypeFor(arrayLayers, sampleCount > 1,
                    (usage & GpuTextureUsage.Cubemap) != 0, arrayView),
                MetalFormats.ToTextureUsage(usage),
                // PRIVATE for every real texture (M-M2), reproducing the incumbent. Not a performance choice on
                // unified memory, where Private and Shared cost the same to read: it is what the goldens were
                // baked through, and a Shared texture would additionally be a CPU-writable surface with no caller.
                MTLStorageMode.Private,
                (usage & GpuTextureUsage.DepthStencil) != 0,
                // ZERO. See the class note: nothing this seam can express narrows a texture, so no MTLTexture view
                // object is created for any of them.
                ViewCount: 0);
        }
    }
}
