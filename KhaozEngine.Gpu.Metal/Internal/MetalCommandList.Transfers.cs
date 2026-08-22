using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TRANSFER FAMILY: buffer copies, texture copies, mip generation and the multisample resolve. Work
    /// breakdown row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>FOUR OF THE FIVE OPEN A BLIT ENCODER AND GET M-A5 FOR FREE. THE RESOLVE DOES NOT, AND IT IS THE
    /// ONE MEMBER THAT ENDS THE OPEN PASS ITSELF.</b> A blit is a DIFFERENT kind from the one a draw uses, so
    /// <see cref="MetalEncoderScope.EnsureBlitEncoder"/> ends whatever is open on the way and the invariant is the
    /// scope's rather than a line repeated in four places, which is the decision row 12 recorded when it did NOT
    /// add an <c>EndPass</c> for these callers to call. The resolve opens a RENDER encoder, which is the SAME kind
    /// a draw uses, so <see cref="MetalEncoderScope.EnsureRenderEncoder"/> short-circuits on an open pass and hands
    /// the caller's own scene encoder straight back with the resolve descriptor unused. Row 12's comment said "a
    /// resolve opens a blit one" and it was wrong. So <see cref="ResolveTexture"/> calls
    /// <see cref="MetalEncoderScope.EnsureNoEncoder"/> itself, first, and the incumbent does the same thing for
    /// the same reason (<c>ResolveTextureCore</c> is <c>EnsureNoBlitEncoder</c> then <c>EnsureNoRenderPass</c>
    /// then a DIRECT encoder creation).</para>
    ///
    /// <para><b>THE ARITHMETIC IS NOT HERE.</b> Which of the four staging cases a copy is, which subresource each
    /// side names and what byte offsets and pitches the staging side supplies are
    /// <see cref="MetalTransferPlan"/>'s, over <see cref="MetalStagingLayout"/>'s reproduction of the incumbent's
    /// software layout. That is deliberate and it is the highest-risk parity surface in the backend: every golden
    /// reads back through one of these copies, and a wrong pitch garbles all 36 at once with no error anywhere.
    /// What is left here is resolving the seam's resources and the refusals that need a real texture to state.
    /// </para>
    ///
    /// <para><b>A SEPARATE PARTIAL</b> for <c>MetalCommandList.Passes.cs</c>'s reason: transfers are a different
    /// subsystem from the recording lifecycle, and the design's own KESIZE warning for this phase is that the
    /// incumbent's <c>MTLCommandList.cs</c> is 1163 lines against an 800-line cap.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        /// <inheritdoc/>
        /// <remarks>
        /// <c>-copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>, under section 9.3's alignment
        /// ruling: BOTH offsets must be multiples of four and are refused by name when they are not, while the
        /// SIZE is padded up, which is the pad the incumbent already applies on its own aligned path.
        /// <see cref="MetalCopyAlignment"/> carries the whole ruling and the proof that the pad lands inside both
        /// allocations.
        /// <para>
        /// THE INCUMBENT'S UNALIGNED PATH IS DELIBERATELY NOT REPRODUCED. It routes any unaligned copy through an
        /// embedded compute shader driven by a dedicated compute pipeline, and shipping a second metallib for a
        /// case no shipped call site produces is the unreachable-code reproduction G1 declined once already.
        /// <c>MetalCopyBufferCallSiteTests</c> is what says no call site produces one.
        /// </para>
        /// <para>
        /// AND SINCE 17.40.0 THE REFUSAL IS NO LONGER THIS BACKEND'S ALONE
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/684">#684</see>). The offset rule moved up
        /// to the seam, so Veldrid, native Vulkan and native Direct3D 11 refuse the same offsets with the same
        /// words out of <c>GpuCopyAlignment</c>, and the incumbent no longer reaches the compute path above from
        /// this engine at all.
        /// </para>
        /// </remarks>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(src);
            ArgumentNullException.ThrowIfNull(dst);

            MetalBuffer source = MetalResourceOwnership.Require<MetalBuffer>(src, _liveness, nameof(src));
            MetalBuffer destination = MetalResourceOwnership.Require<MetalBuffer>(dst, _liveness, nameof(dst));
            RequireRecording("Copying between buffers");

            const string What = "A native Metal buffer copy";
            MetalBufferPolicy.RequireWriteFits(srcOffsetBytes, sizeInBytes, source.SizeInBytes);
            MetalBufferPolicy.RequireWriteFits(dstOffsetBytes, sizeInBytes, destination.SizeInBytes);
            MetalCopyAlignment.RequireAlignedOffset(srcOffsetBytes, nameof(srcOffsetBytes), What, "source");
            MetalCopyAlignment.RequireAlignedOffset(dstOffsetBytes, nameof(dstOffsetBytes), What, "destination");

            // A ZERO-BYTE COPY RECORDS NOTHING rather than reaching the selector, and it is not the Vulkan
            // sibling's throw. There a region of size 0 is a documented VUID violation the driver refuses, so a
            // refusal is the only honest answer. Metal's copy takes a length and a length of zero is a no-op, so
            // refusing would be this backend inventing a rule, and the seam's own callers legitimately compute a
            // length that can come out zero.
            if (sizeInBytes == 0) return;

            IntPtr encoder = _encoders.EnsureBlitEncoder();
            if (encoder == IntPtr.Zero) return;
            if (source.Handle.Handle == IntPtr.Zero || destination.Handle.Handle == IntPtr.Zero) return;

            _blit.CopyBufferToBuffer(encoder, source.Handle.Handle, srcOffsetBytes, destination.Handle.Handle,
                dstOffsetBytes, MetalCopyAlignment.PaddedSize(sizeInBytes));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// EVERY MIP LEVEL AND EVERY ARRAY LAYER, one region each, which is what a whole-texture copy means and
        /// what the readback path needs when it copies a mipped render target into a staging texture. The two
        /// textures must agree on shape, because a copy that silently clipped would produce a golden that is
        /// subtly wrong rather than an error.
        /// </remarks>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            (MetalTexture source, MetalTexture destination) = BeginTextureCopy(src, dst, "Copying a texture");
            RequireMatchingShape(source, destination);

            IntPtr encoder = _encoders.EnsureBlitEncoder();
            if (encoder == IntPtr.Zero) return;

            for (uint layer = 0; layer < source.ArrayLayers; layer++)
            {
                for (uint level = 0; level < source.MipLevels; level++)
                {
                    Region(encoder, source, level, layer, destination, level, layer,
                        MetalTransferPlan.MipDimension(source.Width, level),
                        MetalTransferPlan.MipDimension(source.Height, level));
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>The mip-0, layer-0 destination form, which is what reading one level of a texture array back
        /// to the CPU is.</remarks>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
            => CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, 0, 0, width, height);

        /// <inheritdoc/>
        /// <remarks>
        /// THE GENERAL FORM: one mip level and one array layer on each side. Its own use is seeding the base level
        /// of a MIPPED texture from a single-mip one written by compute, because a storage-image binding must
        /// cover exactly one mip level, so a compute-written map that also needs a chain has to be two textures
        /// with a copy between them.
        /// </remarks>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        {
            (MetalTexture source, MetalTexture destination) =
                BeginTextureCopy(src, dst, "Copying a texture subresource");

            RequireSubresource(source, srcMipLevel, srcArrayLayer, "source");
            RequireSubresource(destination, dstMipLevel, dstArrayLayer, "destination");

            IntPtr encoder = _encoders.EnsureBlitEncoder();
            if (encoder == IntPtr.Zero) return;

            Region(encoder, source, srcMipLevel, srcArrayLayer, destination, dstMipLevel, dstArrayLayer, width,
                height);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <c>-generateMipmapsForTexture:</c>, the WHOLE chain in one call, which is where Metal is shorter than
        /// both siblings rather than longer: Vulkan generates a chain as a loop of <c>vkCmdBlitImage</c> with a
        /// layout transition per level and a filter to pick, and there is none of that here. What is left is the
        /// guard, and the guard is real: a staging texture is an <c>MTLBuffer</c> with a software layout and has
        /// no <c>MTLTexture</c> to generate anything from.
        /// </remarks>
        public void GenerateMipmaps(IGpuTexture texture)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(texture);

            MetalTexture target = MetalResourceOwnership.Require<MetalTexture>(texture, _liveness,
                nameof(texture));
            RequireRecording("Generating a mip chain");
            RequireMipChain(target);

            IntPtr encoder = _encoders.EnsureBlitEncoder();
            if (encoder == IntPtr.Zero) return;
            if (target.Handle.Handle == IntPtr.Zero) return;

            _blit.GenerateMipmaps(encoder, target.Handle.Handle);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// M-C4's STANDALONE RESOLVE ENCODER, reproduced from the incumbent's <c>ResolveTextureCore</c>: an EMPTY
        /// render pass whose one colour attachment is the MSAA source at <c>loadAction = Load</c> and
        /// <c>storeAction = MultisampleResolve</c>, with the destination as its resolve texture, opened at mip 0
        /// layer 0 and immediately ended.
        /// <para>
        /// IT DESTROYS THE SOURCE'S CONTENTS, which the incumbent's own TODO says and which diverges from what
        /// <c>ResolveSubresource</c> and <c>vkCmdResolveImage</c> do. It is reproduced anyway: the engine's MSAA
        /// sources are re-cleared at the start of the next frame's pass, discarding is the bandwidth-correct
        /// answer on this architecture, and it is what <c>scene3d_hdr_msaa</c> was baked under. The divergence is
        /// documented in the package README so a consumer that ever needs the source preserved finds a property
        /// rather than a surprise.
        /// </para>
        /// <para>
        /// FOLDING THE RESOLVE INTO THE PRODUCING PASS'S STORE ACTION IS THE METAL-NATIVE ANSWER AND IT IS NOT
        /// TAKEN HERE (https://github.com/APKiwiOrg/KhaozEngine/issues/596). It removes a whole encoder and it
        /// changes what a producing pass writes out, which would make gate 1's golden A/B unreadable in the same
        /// phase as M-A2's rendering change.
        /// </para>
        /// <para>
        /// IT OPENS A RENDER ENCODER OUTSIDE <see cref="MetalRenderPassSchedule"/>, WHICH IS THE ONE PLACE IN THE
        /// BACKEND THAT DOES, and the schedule's own invariant survives it because the encoder is opened and
        /// ended inside this call. That invariant (a pass is never both OPEN and owed a clear) is only ever read
        /// at <c>EndPass</c>, and by the time control returns here nothing is open at all, so no caller can
        /// observe the intermediate state.
        /// </para>
        /// <para>
        /// AND IT ENDS THE OPEN PASS ITSELF, WHICH IS THE ONE PLACE IN THIS FILE THAT HAS TO. See the type
        /// remarks: a resolve opens the SAME encoder kind a draw uses, so the scope's Ensure would short-circuit
        /// on an open pass, hand the caller's scene encoder back and DISCARD the resolve descriptor, and the
        /// <c>MultisampleResolve</c> store action would never be set on anything. Nothing about that failure is
        /// loud: the pass ends normally, the buffer completes with a nil error, and the destination keeps whatever
        /// it held. <see cref="MetalEncoderScope.EnsureNoEncoder"/> is the parity-correct call rather than
        /// <c>EndPass</c>: the incumbent's <c>ResolveTextureCore</c> calls plain <c>EnsureNoRenderPass</c> and
        /// leaves a clear-only pass's pending clears OWED across the resolve, which is M-A3's flush staying at its
        /// two forcing sites (a framebuffer change and <c>End</c>) rather than gaining a third here.
        /// </para>
        /// </remarks>
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(src);
            ArgumentNullException.ThrowIfNull(dst);

            MetalTexture source = MetalResourceOwnership.Require<MetalTexture>(src, _liveness, nameof(src));
            MetalTexture destination = MetalResourceOwnership.Require<MetalTexture>(dst, _liveness, nameof(dst));
            RequireRecording("Resolving a multisampled texture");
            RequireResolvable(source, destination);

            StandaloneResolvePass(source.Handle.Handle, destination.Handle.Handle);
        }

        /// <summary>
        /// THE RESOLVE'S ENCODER SEQUENCE, with the two textures already resolved to handles and every refusal
        /// already spent. Called by <see cref="ResolveTexture"/> and, for the reason
        /// <c>MetalTransferPathTests</c> records in full, by the device-free row that asserts the end-first
        /// ordering: a <see cref="MetalTexture"/> has a private constructor and one factory that takes an
        /// <c>MTLDevice</c>, so the public member cannot be driven off a device at all, and the ordering it
        /// depends on is a decision rather than a driver call.
        /// </summary>
        /// <param name="sourceTexture">The multisampled <c>MTLTexture</c> the pass loads and resolves out of.
        /// </param>
        /// <param name="destinationTexture">The single-sample <c>MTLTexture</c> named as the resolve target.
        /// </param>
        internal void StandaloneResolvePass(IntPtr sourceTexture, IntPtr destinationTexture)
        {
            // END WHATEVER IS OPEN BEFORE THE DESCRIPTOR IS EVEN BUILT, which is the incumbent's own order and the
            // half the scope cannot do for this member. Plain EnsureNoEncoder, not EndPass: see the remarks above.
            _encoders.EnsureNoEncoder();

            IntPtr descriptor = _render.CreateResolveDescriptor(sourceTexture, destinationTexture);
            if (descriptor == IntPtr.Zero) return;

            try
            {
                // THE PASS IS OPENED AND ENDED WITH NOTHING IN IT, which is what a resolve IS on this API: the
                // store action does the work at the end of the encoder, so there is no command to record into it.
                if (_encoders.EnsureRenderEncoder(descriptor) == IntPtr.Zero) return;

                _encoders.EnsureNoEncoder();
            }
            finally
            {
                // EXACTLY ONE RELEASE PER ACQUISITION AT EVERY EXIT, including the one where the encoder came
                // back nil, which is the schedule's own rule for its own descriptor.
                _render.ReleaseRenderPassDescriptor(descriptor);
            }
        }

        // ---- The shared halves -------------------------------------------------------------------------------

        // ONE REGION, IN WHICHEVER OF THE FOUR SHAPES THIS PAIR IS. The staging side supplies the buffer terms out
        // of its own software layout and the texture side supplies the subresource, which is the split that is
        // easy to get backwards: the pitches belong to the STAGING mip and the level and slice passed to the
        // selector belong to the TEXTURE, and they need not be the same numbers.
        void Region(IntPtr encoder, MetalTexture source, uint sourceLevel, uint sourceLayer,
            MetalTexture destination, uint destinationLevel, uint destinationLayer, uint width, uint height)
        {
            switch (MetalTransferPlan.CaseFor(source.IsStaging, destination.IsStaging))
            {
                case MetalTransferCase.TextureToTexture:
                    _blit.CopyTextureToTexture(encoder, source.Handle.Handle, destination.Handle.Handle,
                        MetalTransferPlan.TextureRegion(sourceLevel, sourceLayer, destinationLevel,
                            destinationLayer, width, height));
                    return;

                case MetalTransferCase.TextureToBuffer:
                    _blit.CopyTextureToBuffer(encoder, source.Handle.Handle, destination.StagingBuffer.Handle,
                        MetalTransferPlan.ReadbackRegion(destination.Shape, destinationLevel, destinationLayer,
                            sourceLevel, sourceLayer, width, height));
                    return;

                case MetalTransferCase.BufferToTexture:
                    _blit.CopyBufferToTexture(encoder, source.StagingBuffer.Handle, destination.Handle.Handle,
                        MetalTransferPlan.UploadRegion(source.Shape, sourceLevel, sourceLayer, destinationLevel,
                            destinationLayer, width, height));
                    return;

                default:
                    StagingToStaging(encoder, source, sourceLevel, sourceLayer, destination, destinationLevel,
                        destinationLayer);
                    return;
            }
        }

        // BOTH SIDES ARE MTLBuffers, so this is a plain byte copy between two software layouts and the
        // subresource offsets are the whole of what it needs. It is the one arm that inherits section 9.3's
        // alignment ruling second-hand: the offsets come from the layout rather than from a caller, so an
        // unaligned one is a FORMAT whose row pitch is not a multiple of four rather than a caller mistake, and
        // the refusal names that.
        //
        // AND IT IS THE ONE COPY WHOSE SIZE IS NOT PADDED UP. The pad is safe between two IGpuBuffers because
        // both were allocated rounded up to four, and neither side here is one: a staging texture's buffer is
        // allocated at exactly MetalStagingLayout.TotalBytes with its subresources packed end to end, so four
        // padding bytes land in the NEXT subresource, or past the allocation on the last one. See
        // MetalTransferPlan.StagingToStaging, which is where the rule and the incumbent's agreement with it live.
        void StagingToStaging(IntPtr encoder, MetalTexture source, uint sourceLevel, uint sourceLayer,
            MetalTexture destination, uint destinationLevel, uint destinationLayer)
        {
            (ulong from, ulong to, ulong size) = MetalTransferPlan.StagingToStaging(
                source.Shape, sourceLevel, sourceLayer, destination.Shape, destinationLevel, destinationLayer);

            if (size == 0) return;

            MetalCopyAlignment.RequireAlignedOffset(from, nameof(source),
                "A native Metal staging-to-staging texture copy", "source subresource");
            MetalCopyAlignment.RequireAlignedOffset(to, nameof(destination),
                "A native Metal staging-to-staging texture copy", "destination subresource");

            _blit.CopyBufferToBuffer(encoder, source.StagingBuffer.Handle, from,
                destination.StagingBuffer.Handle, to, (uint)size);
        }

        // THE THREE THINGS EVERY TEXTURE COPY DOES BEFORE ITS FIRST REGION, minus the encoder, which the caller
        // opens after its own shape refusals so a refused copy never spends a boundary.
        (MetalTexture Source, MetalTexture Destination) BeginTextureCopy(IGpuTexture src, IGpuTexture dst,
            string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(src);
            ArgumentNullException.ThrowIfNull(dst);

            MetalTexture source = MetalResourceOwnership.Require<MetalTexture>(src, _liveness, nameof(src));
            MetalTexture destination = MetalResourceOwnership.Require<MetalTexture>(dst, _liveness, nameof(dst));
            RequireRecording(what);

            return (source, destination);
        }

        static void RequireMatchingShape(MetalTexture source, MetalTexture destination)
        {
            if (source.Width == destination.Width && source.Height == destination.Height
                && source.MipLevels == destination.MipLevels && source.ArrayLayers == destination.ArrayLayers
                && source.Format == destination.Format)
            {
                return;
            }

            throw new ArgumentException(
                "A native Metal whole-texture copy was asked for between two textures that do not agree on "
                + "width, height, mip count, array layer count and format. A whole copy names every subresource "
                + "on both sides, so a mismatch is a copy that would clip or run off the end rather than one this "
                + "backend can decide how to narrow. Use CopyTextureSubresource for a region.",
                nameof(destination));
        }

        static void RequireSubresource(MetalTexture texture, uint mipLevel, uint arrayLayer, string side)
        {
            if (mipLevel < texture.MipLevels && arrayLayer < texture.ArrayLayers) return;

            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                "A native Metal subresource copy named mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture) + " and array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture) + " of its " + side + ", which has "
                + texture.MipLevels.ToString(CultureInfo.InvariantCulture) + " mip level(s) and "
                + texture.ArrayLayers.ToString(CultureInfo.InvariantCulture)
                + " array layer(s). A subresource index past the end is a copy that names memory the texture does "
                + "not have, and the software staging layout would compute an offset outside its own buffer for "
                + "it rather than clipping.");
        }

        static void RequireMipChain(MetalTexture texture)
        {
            if (texture.MipLevels > 1 && !texture.IsStaging) return;

            throw new ArgumentException(
                "A native Metal mip generation was asked for on a texture with "
                + texture.MipLevels.ToString(CultureInfo.InvariantCulture) + " mip level(s)"
                + (texture.IsStaging ? " that is a STAGING texture" : "")
                + ". generateMipmapsForTexture: fills levels 1 and up from level 0, so the texture needs more "
                + "than one level and needs a real MTLTexture: a staging texture is an MTLBuffer with a software "
                + "subresource layout (M-C5) and has no texture to generate from. Create it with "
                + "GpuTextureUsage." + nameof(GpuTextureUsage.GenerateMipmaps) + " and a mip count above 1.",
                nameof(texture));
        }

        static void RequireResolvable(MetalTexture source, MetalTexture destination)
        {
            if (source.SampleCount > 1 && destination.SampleCount == 1 && !source.IsStaging
                && !destination.IsStaging && source.Width == destination.Width
                && source.Height == destination.Height && source.Format == destination.Format)
            {
                return;
            }

            throw new ArgumentException(
                "A native Metal multisample resolve was asked for from a texture at "
                + source.SampleCount.ToString(CultureInfo.InvariantCulture) + " sample(s) into one at "
                + destination.SampleCount.ToString(CultureInfo.InvariantCulture)
                + ". A resolve averages the samples of a MULTISAMPLED attachment into a SINGLE-SAMPLE one of the "
                + "same width, height and format, and neither side may be a staging texture, which is an "
                + "MTLBuffer with no attachment handle at all. An out-of-range sample count is refused at TEXTURE "
                + "CREATION rather than here, which is C4's departure inherited for C4's reason: the engine "
                + "clamps upstream against GpuCapabilities.MaxMsaaSampleCount, so nothing legitimate reaches "
                + "either throw, and a silent MSAA downgrade presents as a golden mismatch that reads like a "
                + "rendering bug.",
                nameof(destination));
        }
    }
}
