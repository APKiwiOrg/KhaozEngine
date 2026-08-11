using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE COPY ARITHMETIC, DEVICE-FREE. Work breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), over <see cref="MetalTransferPlan"/>.
    ///
    /// <para><b>WHY THIS FILE EXISTS AT ALL, given the goldens.</b> Every golden in the suite reaches the CPU
    /// through one of these three region shapes, so a wrong pitch or a swapped level garbles all of them at once
    /// and does it silently: the copy succeeds, the pointer is valid, and the texels are in the wrong places.
    /// That is the same failure class <see cref="MetalStagingLayoutTableTests"/> exists for one level down, and
    /// the answer is the same: pin the arithmetic before a single golden runs, on a leg with no Metal in it.</para>
    ///
    /// <para><b>THE NUMBERS COME FROM <see cref="MetalStagingLayout"/> RATHER THAN FROM THIS FILE'S HEAD wherever
    /// they can.</b> Re-deriving a pitch here would mean two copies of one formula agreeing with each other and
    /// possibly with nothing else. Where a literal IS typed out it is quoted from the checked-in table in
    /// <see cref="MetalStagingLayoutTableTests"/>, which was generated from Veldrid <c>4.9.103</c>'s own
    /// functions, and the row it came from is named on the test.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either the four staging cases have been re-ordered or re-mapped (which
    /// sends a readback down the upload selector), or the buffer and texture halves of a region have been crossed
    /// (which is the split the type's own doc calls the one that is easy to get backwards), or the readback and
    /// upload depth-pitch asymmetry has been smoothed away, or the staging-to-staging arm has stopped taking the
    /// SMALLER of the two subresource sizes, which turns a mismatched pair from a short copy into an overrun, or
    /// that arm's size has started being PADDED up to four, which writes into the subresource after the one the
    /// copy was aimed at.</para>
    /// </summary>
    public sealed class MetalTransferPlanTests
    {
        // ONE SHAPE THAT IS BOTH MIPPED AND LAYERED AND NON-POWER-OF-TWO, which is where a mip offset and a layer
        // pitch can disagree, and which the checked-in layout table already carries rows for. See
        // MetalStagingLayoutTableTests: R8G8B8A8UNorm 33x17 with 4 mip levels and 3 array layers.
        static readonly MetalStagingShape Mixed = new(33, 17, 4, 3, GpuPixelFormat.R8G8B8A8UNorm);

        // TWO SHAPES OF DIFFERENT SIZES, for the staging-to-staging arm. Same texel dimensions and layer count,
        // different formats, so their subresources sit at different offsets AND have different sizes, which is
        // the only way "the smaller of the two" is observable at all.
        static readonly MetalStagingShape Wide = new(64, 64, 1, 4, GpuPixelFormat.R8G8B8A8UNorm);
        static readonly MetalStagingShape Narrow = new(64, 64, 1, 4, GpuPixelFormat.R8UNorm);

        // ---- Which of the four shapes a copy is --------------------------------------------------------------

        /// <summary>
        /// ALL FOUR COMBINATIONS, EXPLICITLY, because the enum is a selector choice and a re-ordering of its
        /// members would compile silently. A staging texture is an <c>MTLBuffer</c> with a software layout (M-C5),
        /// so the side that is staging decides which of the three blit selectors the copy reaches, and getting
        /// this wrong sends a readback through <c>copyFromBuffer:toTexture:</c> with the roles reversed.
        /// <para>
        /// A RED RUN HERE IS THE LOUDEST FAILURE IN THE FILE and also the least likely to be caught anywhere else:
        /// the wrong selector on a device is a validation error or a scrambled surface, and on the fakes it is a
        /// copy recorded in the wrong list.
        /// </para>
        /// </summary>
        [Fact]
        public void CaseFor_MapsAllFourStagingCombinations()
        {
            Assert.Equal(MetalTransferCase.TextureToTexture,
                MetalTransferPlan.CaseFor(sourceIsStaging: false, destinationIsStaging: false));

            Assert.Equal(MetalTransferCase.TextureToBuffer,
                MetalTransferPlan.CaseFor(sourceIsStaging: false, destinationIsStaging: true));

            Assert.Equal(MetalTransferCase.BufferToTexture,
                MetalTransferPlan.CaseFor(sourceIsStaging: true, destinationIsStaging: false));

            Assert.Equal(MetalTransferCase.BufferToBuffer,
                MetalTransferPlan.CaseFor(sourceIsStaging: true, destinationIsStaging: true));
        }

        /// <summary>
        /// AND THE READBACK CASE IS THE ONE NAMED AFTER THE DESTINATION, which is the pair a reader is most likely
        /// to swap. A real texture into a staging texture is <see cref="MetalTransferCase.TextureToBuffer"/>, and
        /// that is EVERY golden's readback. Called out on its own so the claim is legible rather than buried in
        /// the four-row table above.
        /// </summary>
        [Fact]
        public void CaseFor_ReadbackIsTextureToBufferAndUploadIsBufferToTexture()
        {
            Assert.Equal(MetalTransferCase.TextureToBuffer,
                MetalTransferPlan.CaseFor(sourceIsStaging: false, destinationIsStaging: true));

            Assert.NotEqual(MetalTransferCase.BufferToTexture,
                MetalTransferPlan.CaseFor(sourceIsStaging: false, destinationIsStaging: true));
        }

        // ---- The mip dimension -------------------------------------------------------------------------------

        /// <summary>
        /// <see cref="MetalTransferPlan.MipDimension"/> IS <see cref="MetalStagingLayout.MipDimension"/> AND NOT A
        /// SECOND COPY OF IT. The plan names it through so a caller composing a whole-texture copy reads one type
        /// rather than two, and the value that copy passes to the selector has to be the same number the staging
        /// layout computed its pitches from. A second implementation that halved differently would produce a copy
        /// whose extent and whose row pitch disagree, which is a torn image rather than an error.
        /// <para>
        /// THE SPREAD IS THE ONE THAT CATCHES A SHIFT REPLACING THE LOOP: a truncating halve (33 and 100), a level
        /// count that runs past 1 texel, and level 0 itself.
        /// </para>
        /// </summary>
        [Fact]
        public void MipDimension_IsTheStagingLayoutsOwnAnswer()
        {
            uint[] dimensions = [1, 2, 3, 17, 33, 60, 64, 100, 256];

            foreach (uint dimension in dimensions)
            {
                for (uint level = 0; level < 12; level++)
                {
                    Assert.Equal(MetalStagingLayout.MipDimension(dimension, level),
                        MetalTransferPlan.MipDimension(dimension, level));
                }
            }
        }

        /// <summary>The values themselves, for a reader who wants to see one chain written out. 64 halves to 1 by
        /// level 6 and stays there, and 33 truncates to 16 rather than rounding up.</summary>
        [Theory]
        [InlineData(64u, 0u, 64u)]
        [InlineData(64u, 1u, 32u)]
        [InlineData(64u, 6u, 1u)]
        [InlineData(64u, 9u, 1u)]
        [InlineData(33u, 1u, 16u)]
        [InlineData(1u, 3u, 1u)]
        public void MipDimension_HalvesAndFloorsAtOne(uint largest, uint level, uint expected)
            => Assert.Equal(expected, MetalTransferPlan.MipDimension(largest, level));

        // ---- The buffer-to-image split -----------------------------------------------------------------------

        /// <summary>
        /// THE SPLIT THE TYPE'S OWN DOC CALLS THE EASY ONE TO GET BACKWARDS, with the two sides deliberately
        /// naming DIFFERENT subresources so a crossed assignment cannot pass. The offset and both pitches belong
        /// to the STAGING side's mip and layer, and <c>Level</c> and <c>Layer</c> belong to the TEXTURE side's.
        /// <para>
        /// STAGING MIP 1 LAYER 0 INTO TEXTURE MIP 2 LAYER 1, which is a legal call through
        /// <c>CopyTextureSubresource</c>: nothing requires the two sides to name the same subresource, and reading
        /// one level of a texture array back into a plain staging texture is exactly that shape.
        /// </para>
        /// <para>
        /// WHAT A RED RUN MEANS: the copy will address the wrong bytes of the staging buffer, or the wrong
        /// subresource of the texture, or both. Neither is an error on a device.
        /// </para>
        /// </summary>
        [Fact]
        public void ReadbackRegion_TakesItsPitchesFromTheStagingMipAndItsLevelFromTheTexture()
        {
            MetalSubresourceLayout staging = MetalStagingLayout.For(Mixed, mipLevel: 1, arrayLayer: 0);

            MetalBufferImageRegion region = MetalTransferPlan.ReadbackRegion(Mixed, stagingLevel: 1,
                stagingLayer: 0, textureLevel: 2, textureLayer: 1, width: 8, height: 4);

            // The BUFFER half is the staging side's, at ITS level and layer.
            Assert.Equal(staging.Offset, region.BufferOffset);
            Assert.Equal(staging.RowPitch, region.BytesPerRow);
            Assert.Equal(staging.DepthPitch, region.BytesPerImage);

            // The TEXTURE half is the other side's, and the two are genuinely different numbers.
            Assert.Equal(2u, region.Level);
            Assert.Equal(1u, region.Layer);
            Assert.Equal(8u, region.Width);
            Assert.Equal(4u, region.Height);
        }

        /// <summary>
        /// THE SAME SPLIT ON THE UPLOAD, and the same deliberately mismatched pair. Seeding one mip level of a
        /// real texture from a staging texture written at a different level is the shape the general
        /// <c>CopyTextureSubresource</c> overload exists for.
        /// </summary>
        [Fact]
        public void UploadRegion_TakesItsPitchFromTheStagingMipAndItsLevelFromTheTexture()
        {
            MetalSubresourceLayout staging = MetalStagingLayout.For(Mixed, mipLevel: 1, arrayLayer: 0);

            MetalBufferImageRegion region = MetalTransferPlan.UploadRegion(Mixed, stagingLevel: 1, stagingLayer: 0,
                textureLevel: 2, textureLayer: 1, width: 8, height: 4);

            Assert.Equal(staging.Offset, region.BufferOffset);
            Assert.Equal(staging.RowPitch, region.BytesPerRow);

            Assert.Equal(2u, region.Level);
            Assert.Equal(1u, region.Layer);
            Assert.Equal(8u, region.Width);
            Assert.Equal(4u, region.Height);
        }

        /// <summary>
        /// THE ASYMMETRY THAT IS REPRODUCED RATHER THAN CHOSEN: <c>BytesPerImage</c> is the staging side's DEPTH
        /// PITCH on a readback and ZERO on an upload. The incumbent's <c>CopyTextureCore</c> zeroes
        /// <c>sourceBytesPerImage</c> for anything that is not a 3D texture, and this seam has no 3D texture,
        /// while its readback branch passes the real depth pitch.
        /// <para>
        /// IT IS ASSERTED ON BOTH SIDES AT ONCE BECAUSE THE DIVERGENCE IS INVISIBLE ANYWHERE ELSE. For the
        /// single-slice copies this backend records the two readings are interchangeable, so a device would not
        /// notice either value being changed to the other, and no golden would move. This row is the only thing
        /// that says the reproduction is still a reproduction.
        /// </para>
        /// </summary>
        [Fact]
        public void BytesPerImage_IsTheDepthPitchOnAReadbackAndZeroOnAnUpload()
        {
            MetalSubresourceLayout staging = MetalStagingLayout.For(Mixed, mipLevel: 1, arrayLayer: 0);

            MetalBufferImageRegion readback = MetalTransferPlan.ReadbackRegion(Mixed, stagingLevel: 1,
                stagingLayer: 0, textureLevel: 1, textureLayer: 0, width: 16, height: 8);
            MetalBufferImageRegion upload = MetalTransferPlan.UploadRegion(Mixed, stagingLevel: 1, stagingLayer: 0,
                textureLevel: 1, textureLayer: 0, width: 16, height: 8);

            Assert.Equal(staging.DepthPitch, readback.BytesPerImage);
            Assert.NotEqual(0ul, readback.BytesPerImage);
            Assert.Equal(0ul, upload.BytesPerImage);

            // AND NOTHING ELSE DIFFERS between the two, which is what makes this an asymmetry of one field rather
            // than two unrelated region builders.
            Assert.Equal(readback.BufferOffset, upload.BufferOffset);
            Assert.Equal(readback.BytesPerRow, upload.BytesPerRow);
            Assert.Equal(readback.Level, upload.Level);
            Assert.Equal(readback.Layer, upload.Layer);
            Assert.Equal(readback.Width, upload.Width);
            Assert.Equal(readback.Height, upload.Height);
        }

        /// <summary>
        /// THE SAME CLAIM AGAINST TYPED-OUT NUMBERS, so the two rows above cannot both pass on a staging layout
        /// that is itself wrong. These four literals are quoted from the checked-in table in
        /// <see cref="MetalStagingLayoutTableTests"/>, which was generated from Veldrid <c>4.9.103</c>'s own nine
        /// functions rather than from the implementation under test: the row is
        /// <c>R8G8B8A8UNorm 33x17, 4 mip levels, 3 array layers, subresource (mip 1, layer 0)</c>, whose offset is
        /// 2244, row pitch 64, depth pitch 512 and size 512.
        /// </summary>
        [Fact]
        public void ReadbackRegion_MatchesTheCheckedInTablesOwnNumbers()
        {
            MetalBufferImageRegion region = MetalTransferPlan.ReadbackRegion(Mixed, stagingLevel: 1,
                stagingLayer: 0, textureLevel: 3, textureLayer: 2, width: 16, height: 8);

            Assert.Equal(2244ul, region.BufferOffset);
            Assert.Equal(64ul, region.BytesPerRow);
            Assert.Equal(512ul, region.BytesPerImage);

            // The texture side is untouched by any of that, which is the whole point of the split.
            Assert.Equal(3u, region.Level);
            Assert.Equal(2u, region.Layer);
        }

        /// <summary>
        /// AND ACROSS EVERY SUBRESOURCE OF THE MIXED SHAPE, so one lucky row cannot carry the claim. The buffer
        /// half must equal what the staging layout answers for the STAGING pair on all twelve of them, with the
        /// texture pair walked backwards at the same time so the two can never coincide by accident.
        /// </summary>
        [Fact]
        public void EverySubresource_KeepsTheBufferHalfOnTheStagingSide()
        {
            for (uint layer = 0; layer < Mixed.ArrayLayers; layer++)
            {
                for (uint level = 0; level < Mixed.MipLevels; level++)
                {
                    uint textureLevel = Mixed.MipLevels - 1 - level;
                    uint textureLayer = Mixed.ArrayLayers - 1 - layer;

                    MetalSubresourceLayout staging = MetalStagingLayout.For(Mixed, level, layer);
                    MetalBufferImageRegion region = MetalTransferPlan.ReadbackRegion(Mixed, level, layer,
                        textureLevel, textureLayer, width: 1, height: 1);

                    Assert.Equal(staging.Offset, region.BufferOffset);
                    Assert.Equal(staging.RowPitch, region.BytesPerRow);
                    Assert.Equal(staging.DepthPitch, region.BytesPerImage);
                    Assert.Equal(textureLevel, region.Level);
                    Assert.Equal(textureLayer, region.Layer);
                }
            }
        }

        // ---- The texture-to-texture region -------------------------------------------------------------------

        /// <summary>
        /// THE TWO SIDES OF A TEXTURE-TO-TEXTURE REGION STAY ON THEIR OWN SIDES. Every field here is a
        /// pass-through, which is exactly why it needs a row: a pass-through is where a swap costs nothing to
        /// write and produces a copy that runs, succeeds, and moves the wrong subresource. All four values are
        /// distinct so no two of them can be exchanged without this failing.
        /// </summary>
        [Fact]
        public void TextureRegion_KeepsEachSidesSubresourceOnItsOwnSide()
        {
            MetalTextureRegion region = MetalTransferPlan.TextureRegion(sourceLevel: 1, sourceLayer: 2,
                destinationLevel: 3, destinationLayer: 4, width: 5, height: 6);

            Assert.Equal(1u, region.SourceLevel);
            Assert.Equal(2u, region.SourceLayer);
            Assert.Equal(3u, region.DestinationLevel);
            Assert.Equal(4u, region.DestinationLayer);
            Assert.Equal(5u, region.Width);
            Assert.Equal(6u, region.Height);
        }

        // ---- The staging-to-staging arm ----------------------------------------------------------------------

        /// <summary>
        /// BOTH SUBRESOURCE OFFSETS AND THE SMALLER OF THE TWO SIZES. Both sides are <c>MTLBuffer</c>s here, so
        /// there is no texture selector involved at all and the offsets ARE the whole of what the copy needs.
        /// <para>
        /// TAKING THE SMALLER SIZE IS WHAT KEEPS A MISMATCHED PAIR A SHORT COPY RATHER THAN AN OVERRUN, and the
        /// pair below is mismatched on purpose: same dimensions and layer count, different formats, so the wide
        /// side's subresource is four times the narrow one's. A red run here means a copy sized off the wrong side
        /// of the pair, which writes past the destination subresource into whatever follows it in the same
        /// allocation.
        /// </para>
        /// </summary>
        [Fact]
        public void StagingToStaging_ReturnsBothOffsetsAndTheSmallerSize()
        {
            MetalSubresourceLayout from = MetalStagingLayout.For(Wide, mipLevel: 0, arrayLayer: 1);
            MetalSubresourceLayout to = MetalStagingLayout.For(Narrow, mipLevel: 0, arrayLayer: 2);

            (ulong sourceOffset, ulong destinationOffset, ulong size) = MetalTransferPlan.StagingToStaging(
                Wide, sourceLevel: 0, sourceLayer: 1, Narrow, destinationLevel: 0, destinationLayer: 2);

            Assert.Equal(from.Offset, sourceOffset);
            Assert.Equal(to.Offset, destinationOffset);
            Assert.Equal(to.Size, size);

            // And the sizes really do differ, so "the smaller" is a decision rather than a coincidence.
            Assert.True(to.Size < from.Size);
        }

        /// <summary>
        /// THE SAME CALL WITH THE PAIR REVERSED still takes the smaller size, which is what says the choice is
        /// <c>Math.Min</c> rather than "the destination's". A rule that always trusted one side would pass the row
        /// above and overrun here.
        /// </summary>
        [Fact]
        public void StagingToStaging_TakesTheSmallerSizeInEitherDirection()
        {
            MetalSubresourceLayout narrow = MetalStagingLayout.For(Narrow, mipLevel: 0, arrayLayer: 2);

            (ulong sourceOffset, ulong destinationOffset, ulong size) = MetalTransferPlan.StagingToStaging(
                Narrow, sourceLevel: 0, sourceLayer: 2, Wide, destinationLevel: 0, destinationLayer: 1);

            Assert.Equal(narrow.Offset, sourceOffset);
            Assert.Equal(MetalStagingLayout.For(Wide, 0, 1).Offset, destinationOffset);
            Assert.Equal(narrow.Size, size);
        }

        /// <summary>
        /// THE LITERALS, from the same checked-in table, so the arm above cannot pass on a staging layout that is
        /// wrong in both places at once. <see cref="MetalStagingLayoutTableTests"/> carries
        /// <c>R8G8B8A8UNorm 64x64, 1 mip level, 4 array layers</c> with layer 1 at offset 16384 and size 16384,
        /// and <c>R8UNorm 64x64, 1 mip level, 4 array layers</c> with layer 2 at offset 8192 and size 4096.
        /// </summary>
        [Fact]
        public void StagingToStaging_MatchesTheCheckedInTablesOwnNumbers()
        {
            (ulong sourceOffset, ulong destinationOffset, ulong size) = MetalTransferPlan.StagingToStaging(
                Wide, sourceLevel: 0, sourceLayer: 1, Narrow, destinationLevel: 0, destinationLayer: 2);

            Assert.Equal(16384ul, sourceOffset);
            Assert.Equal(8192ul, destinationOffset);
            Assert.Equal(4096ul, size);
        }

        /// <summary>
        /// THE SIZE IS EXACT, AND ON THIS SHAPE A PAD WOULD LAND IN THE NEXT SUBRESOURCE. Section 9.3 rounds a
        /// copy SIZE up to four, which is safe between two <c>IGpuBuffer</c>s because both are allocated rounded
        /// up, and this arm's two sides are staging textures instead: an <c>MTLBuffer</c> allocated at exactly
        /// <see cref="MetalStagingLayout.TotalBytes"/> with its subresources PACKED end to end.
        ///
        /// <para><b>THE SHAPE IS CHOSEN SO THE CROSSING IS REAL RATHER THAN HYPOTHETICAL.</b> <c>R8UNorm</c> at
        /// 3x3 is a 9-byte subresource, so layer 1 starts at byte 9, and a copy into layer 0 padded to 12 would
        /// overwrite the first three bytes of layer 1. On the LAST layer the same pad runs past the allocation.
        /// There is no room to clamp it into either, because this size is already the smaller of the two
        /// subresources, so any pad at all is past the end of one of them.</para>
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> The size came back rounded, which is a copy that silently corrupts
        /// the subresource after the one it was aimed at. <c>CopyTexture</c> survives that by iteration order (it
        /// rewrites what it clobbered on the next pass of the loop) and <c>CopyTextureSubresource</c> does
        /// not.</para>
        /// </summary>
        [Fact]
        public void StagingToStaging_TheSizeIsExactBecauseAPadWouldCrossIntoTheNextSubresource()
        {
            // R8UNorm 3x3, one mip, two layers: a 9-byte subresource, and 9 is not a multiple of four.
            var packed = new MetalStagingShape(3, 3, 1, 2, GpuPixelFormat.R8UNorm);

            (_, ulong destinationOffset, ulong size) = MetalTransferPlan.StagingToStaging(
                packed, sourceLevel: 0, sourceLayer: 0, packed, destinationLevel: 0, destinationLayer: 0);

            Assert.Equal(9ul, size);
            Assert.NotEqual(0ul, size % MetalCopyAlignment.Bytes);

            // WHERE THE PAD WOULD HAVE GONE: the next subresource starts at byte 9 and a padded copy writes 12.
            ulong next = MetalStagingLayout.For(packed, mipLevel: 0, arrayLayer: 1).Offset;
            Assert.Equal(9ul, next);
            Assert.True(destinationOffset + MetalCopyAlignment.PaddedSize((uint)size) > next,
                "the shape has to be one where padding really would cross, or the row asserts nothing");

            // AND THE WHOLE ALLOCATION IS 18 BYTES, so the same pad on the LAST layer runs off the end of it.
            Assert.Equal(18ul, MetalStagingLayout.TotalBytes(packed));
        }

        /// <summary>
        /// AND A MATCHING PAIR MOVES THE WHOLE SUBRESOURCE, which is the case every real readback and every real
        /// staging copy is. Without this row the "smaller" rule could be satisfied by something that always
        /// shortened the copy.
        /// </summary>
        [Fact]
        public void StagingToStaging_MovesTheWholeSubresourceWhenTheShapesAgree()
        {
            (ulong sourceOffset, ulong destinationOffset, ulong size) = MetalTransferPlan.StagingToStaging(
                Mixed, sourceLevel: 2, sourceLayer: 1, Mixed, destinationLevel: 2, destinationLayer: 1);

            MetalSubresourceLayout layout = MetalStagingLayout.For(Mixed, mipLevel: 2, arrayLayer: 1);

            Assert.Equal(layout.Offset, sourceOffset);
            Assert.Equal(layout.Offset, destinationOffset);
            Assert.Equal(layout.Size, size);
        }
    }
}
