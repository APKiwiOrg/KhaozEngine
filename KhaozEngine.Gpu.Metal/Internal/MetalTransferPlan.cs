using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH OF THE FOUR SHAPES A TEXTURE COPY IS, decided by whether each side is a STAGING texture. Work
    /// breakdown row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    /// <para>
    /// IT IS FOUR RATHER THAN ONE BECAUSE A STAGING TEXTURE IS NOT A TEXTURE HERE. Row 6 backs one with an
    /// <c>MTLBuffer</c> and a SOFTWARE subresource layout (M-C5), exactly as the incumbent did, so a copy with a
    /// staging side is a buffer copy on that side and the selector changes with it. The incumbent's
    /// <c>CopyTextureCore</c> has the same four branches for the same reason.
    /// </para>
    /// </summary>
    internal enum MetalTransferCase
    {
        /// <summary>Both sides are real <c>MTLTexture</c>s:
        /// <c>copyFromTexture:...toTexture:...</c>.</summary>
        TextureToTexture,

        /// <summary>A real texture into a staging buffer, which is every golden's readback:
        /// <c>copyFromTexture:...toBuffer:...</c>.</summary>
        TextureToBuffer,

        /// <summary>A staging buffer into a real texture:
        /// <c>copyFromBuffer:...toTexture:...</c>.</summary>
        BufferToTexture,

        /// <summary>Both sides are staging, so this is a plain byte copy between two software layouts and no
        /// texture selector is involved at all.</summary>
        BufferToBuffer,
    }

    /// <summary>
    /// ONE TEXTURE-TO-TEXTURE REGION, in the values <c>copyFromTexture:...toTexture:...</c> takes.
    /// <para>
    /// THE ORIGINS ARE NOT HERE BECAUSE THE SEAM CANNOT EXPRESS ONE. Both <c>CopyTexture</c> and both
    /// <c>CopyTextureSubresource</c> overloads copy from the TOP-LEFT of a subresource, so the source and
    /// destination origins are <c>(0, 0, 0)</c> at every call site and a field for them would be a field that only
    /// ever holds one value. The incumbent's own <c>srcX</c>, <c>srcY</c> and <c>srcZ</c> parameters come from
    /// Veldrid's wider base-class overload, which this seam never reaches.
    /// </para>
    /// </summary>
    /// <param name="SourceLevel">Which mip level of the source.</param>
    /// <param name="SourceLayer">Which array layer, which is Metal's <c>slice</c>.</param>
    /// <param name="DestinationLevel">Which mip level of the destination.</param>
    /// <param name="DestinationLayer">Which array layer of the destination.</param>
    /// <param name="Width">Texels copied on X.</param>
    /// <param name="Height">Texels copied on Y.</param>
    internal readonly record struct MetalTextureRegion(
        uint SourceLevel, uint SourceLayer, uint DestinationLevel, uint DestinationLayer, uint Width,
        uint Height);

    /// <summary>
    /// ONE REGION WITH A BUFFER ON ONE SIDE AND A TEXTURE ON THE OTHER, which serves both directions because the
    /// two selectors take the same terms with the roles swapped.
    ///
    /// <para><b>THE SPLIT IS THE ONE THAT IS EASY TO GET BACKWARDS.</b> <see cref="BytesPerRow"/> and
    /// <see cref="BytesPerImage"/> describe the BUFFER's software layout at the STAGING side's mip level, and
    /// <see cref="Level"/> and <see cref="Layer"/> name the TEXTURE side's subresource. Those need not be the same
    /// numbers: reading mip 2 of a texture array into mip 0 of a staging texture is a legal call through
    /// <c>CopyTextureSubresource</c>, and the pitches then come from the staging side's mip 0 while the level
    /// passed to the selector is 2.</para>
    ///
    /// <para><b><see cref="BytesPerImage"/> IS THE DEPTH PITCH ON A READBACK AND ZERO ON AN UPLOAD, which is the
    /// incumbent's own asymmetry reproduced rather than smoothed.</b> Its <c>CopyTextureCore</c> zeroes
    /// <c>sourceBytesPerImage</c> for anything that is not a 3D texture, and this seam has no 3D texture, while
    /// its readback branch passes the real depth pitch. For the single-slice copies this backend records the two
    /// readings are interchangeable, so the divergence would be invisible, which is exactly why it is reproduced
    /// instead of chosen.</para>
    /// </summary>
    /// <param name="BufferOffset">Where in the staging buffer the subresource starts, from the software layout.
    /// </param>
    /// <param name="BytesPerRow">The staging side's row pitch.</param>
    /// <param name="BytesPerImage">The staging side's depth pitch on a readback, 0 on an upload.</param>
    /// <param name="Level">The TEXTURE side's mip level.</param>
    /// <param name="Layer">The TEXTURE side's array layer.</param>
    /// <param name="Width">Texels copied on X.</param>
    /// <param name="Height">Texels copied on Y.</param>
    internal readonly record struct MetalBufferImageRegion(
        ulong BufferOffset, ulong BytesPerRow, ulong BytesPerImage, uint Level, uint Layer, uint Width,
        uint Height);

    /// <summary>
    /// THE COPY ARITHMETIC, DEVICE-FREE, so the thing that garbles a readback is asserted on every leg rather
    /// than discovered as a scrambled golden. Row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>IT IS A TYPE OF ITS OWN FOR <see cref="MetalStagingLayout"/>'s REASON, one level up.</b> That type
    /// reproduces the incumbent's software layout and is pinned against a checked-in table. This one composes
    /// those numbers into the regions the three copy selectors take, and a mistake here is the same class of
    /// failure: the copy succeeds, the pointer is valid, and the texels are in the wrong places.</para>
    ///
    /// <para><b>EVERY REGION IS ONE SUBRESOURCE.</b> The incumbent looped over array layers inside one call and
    /// this backend loops OUTSIDE, one region per layer, which is the same set of native calls in the same order
    /// with the loop where a test can see it.</para>
    /// </summary>
    internal static class MetalTransferPlan
    {
        /// <summary>Which of the four shapes a copy between these two sides is.</summary>
        internal static MetalTransferCase CaseFor(bool sourceIsStaging, bool destinationIsStaging)
            => (sourceIsStaging, destinationIsStaging) switch
            {
                (false, false) => MetalTransferCase.TextureToTexture,
                (false, true) => MetalTransferCase.TextureToBuffer,
                (true, false) => MetalTransferCase.BufferToTexture,
                (true, true) => MetalTransferCase.BufferToBuffer,
            };

        /// <summary>
        /// A mip level's dimension, which is <c>Util.GetDimension</c> and is already
        /// <see cref="MetalStagingLayout.MipDimension"/>. Named through here so a caller composing a whole-texture
        /// copy reads one type rather than two.
        /// </summary>
        internal static uint MipDimension(uint largestLevelDimension, uint mipLevel)
            => MetalStagingLayout.MipDimension(largestLevelDimension, mipLevel);

        /// <summary>The texture-to-texture region for one subresource pair.</summary>
        internal static MetalTextureRegion TextureRegion(uint sourceLevel, uint sourceLayer,
            uint destinationLevel, uint destinationLayer, uint width, uint height)
            => new(sourceLevel, sourceLayer, destinationLevel, destinationLayer, width, height);

        /// <summary>
        /// The READBACK region: a real texture's subresource into a staging texture's software layout.
        /// </summary>
        /// <param name="stagingShape">The staging side's shape, which the pitches and the offset come from.</param>
        /// <param name="stagingLevel">The staging side's mip level.</param>
        /// <param name="stagingLayer">The staging side's array layer.</param>
        /// <param name="textureLevel">The TEXTURE side's mip level.</param>
        /// <param name="textureLayer">The TEXTURE side's array layer.</param>
        /// <param name="width">Texels on X.</param>
        /// <param name="height">Texels on Y.</param>
        internal static MetalBufferImageRegion ReadbackRegion(in MetalStagingShape stagingShape,
            uint stagingLevel, uint stagingLayer, uint textureLevel, uint textureLayer, uint width, uint height)
        {
            MetalSubresourceLayout layout = MetalStagingLayout.For(stagingShape, stagingLevel, stagingLayer);

            // THE DEPTH PITCH, not zero, which is the incumbent's readback branch: a readback names where the
            // NEXT slice would start, and the staging layout has an answer for that.
            return new MetalBufferImageRegion(layout.Offset, layout.RowPitch, layout.DepthPitch, textureLevel,
                textureLayer, width, height);
        }

        /// <summary>
        /// The UPLOAD region: a staging texture's software layout into a real texture's subresource. Same terms,
        /// with <see cref="MetalBufferImageRegion.BytesPerImage"/> at ZERO, which is the incumbent's own value for
        /// anything that is not a 3D texture.
        /// </summary>
        internal static MetalBufferImageRegion UploadRegion(in MetalStagingShape stagingShape, uint stagingLevel,
            uint stagingLayer, uint textureLevel, uint textureLayer, uint width, uint height)
        {
            MetalSubresourceLayout layout = MetalStagingLayout.For(stagingShape, stagingLevel, stagingLayer);

            return new MetalBufferImageRegion(layout.Offset, layout.RowPitch, 0, textureLevel, textureLayer,
                width, height);
        }

        /// <summary>
        /// The staging-to-staging arm: two software layouts, and the bytes that move between them.
        /// <para>
        /// THE SMALLER OF THE TWO SUBRESOURCE SIZES, which is the Vulkan sibling's shape rather than the
        /// incumbent's per-ROW loop. The incumbent copied row by row because its wider base-class overload can
        /// name a sub-rectangle and a differing row pitch on each side. This seam names whole subresources, and
        /// the two sides compute their pitches through the SAME arithmetic, so for any pair whose shapes agree the
        /// per-row loop and one contiguous copy move identical bytes. Taking the smaller size is what keeps a
        /// mismatched pair a short copy rather than an overrun.
        /// </para>
        /// <para>
        /// <b>AND THE SIZE IS EXACT: THE CALLER PASSES IT TO THE SELECTOR UNPADDED, WHICH IS THE ONE COPY IN THE
        /// BACKEND THAT DOES.</b> Section 9.3's ruling rounds a copy SIZE up to four, and that pad is safe between
        /// two <c>IGpuBuffer</c>s because both were allocated rounded up. Neither side here is one. A staging
        /// texture's <c>MTLBuffer</c> is allocated at exactly <see cref="MetalStagingLayout.TotalBytes"/> with its
        /// subresources PACKED end to end, so a pad either overwrites the first bytes of the NEXT subresource or,
        /// on the last one, runs past the allocation, and the read side runs past the source's end the same way.
        /// There is no room to clamp it into either: this size is the SMALLER of the two subresources, so a pad
        /// that changed it at all would already be past the end of one of them. <c>CopyTexture</c>'s own loop is
        /// saved from that by iteration order alone (each region is followed by the one it would overwrite, which
        /// then rewrites it), and <c>CopyTextureSubresource</c> is not, which is what makes the unpadded size a
        /// rule rather than an optimisation. The incumbent agreed by construction: its staging-to-staging arm
        /// issues one EXACT row-sized copy per row with no padding anywhere.
        /// </para>
        /// </summary>
        internal static (ulong SourceOffset, ulong DestinationOffset, ulong Size) StagingToStaging(
            in MetalStagingShape sourceShape, uint sourceLevel, uint sourceLayer,
            in MetalStagingShape destinationShape, uint destinationLevel, uint destinationLayer)
        {
            MetalSubresourceLayout from = MetalStagingLayout.For(sourceShape, sourceLevel, sourceLayer);
            MetalSubresourceLayout to = MetalStagingLayout.For(destinationShape, destinationLevel,
                destinationLayer);

            return (from.Offset, to.Offset, Math.Min(from.Size, to.Size));
        }
    }
}
