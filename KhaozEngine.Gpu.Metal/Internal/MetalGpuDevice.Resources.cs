using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEVICE'S RESOURCE HALF: the factory, the shared sampler pair, the device-level uploads, and
    /// <c>Map</c> with the read drain of M-C6. Work-breakdown row 6.
    /// <para>
    /// A SEPARATE PARTIAL for the same reason creation is one: the seam surface, the creation policy and the
    /// resource path are three concerns, and a device that must stay under the file-size cap has room for one of
    /// them. Both sibling backends split the same way.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice
    {
        /// <inheritdoc/>
        public IGpuResourceFactory Factory => _factory;

        /// <inheritdoc/>
        /// <remarks>WRAP on all three axes, from <see cref="MetalSharedSamplers"/> and NOT from the engine's
        /// same-named <see cref="GpuSamplerDescription.Point"/> static, which clamps. Reading the wrong one of
        /// those two cost two goldens on the Direct3D 11 leg and is this row's named regression evidence.</remarks>
        public IGpuSampler PointSampler => _pointSampler;

        /// <inheritdoc/>
        /// <remarks>WRAP on all three axes. See <see cref="PointSampler"/>.</remarks>
        public IGpuSampler LinearSampler => _linearSampler;

        /// <summary>
        /// The device-owned setup command buffer (M-M9). Exposed because a test reads its counters, and because
        /// the flush sites are spread across both halves of this device: both <c>Map</c> overloads and
        /// <see cref="WaitForIdle"/> are this row's, and <c>Submit</c> is the command-list row's third one.
        /// <para>
        /// The timeline lives on the other partial, beside the queue and the liveness token, because every fence
        /// sits on it and the submit path is what encodes into it.
        /// </para>
        /// </summary>
        internal MetalSetupCommands Setup => _setup;

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => WriteBuffer(b, offsetBytes, MemoryMarshal.AsBytes(data));

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(data);
            WriteBuffer(b, offsetBytes, MemoryMarshal.AsBytes<T>(data));
        }

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => WriteBuffer(b, offsetBytes, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in data)));

        /// <inheritdoc/>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => UpdateTexture(texture, data, x, y, width, height, 0, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// TWO PATHS, WHICH IS THE INCUMBENT'S OWN <c>if</c>. A STAGING texture is a Shared buffer, so the upload
        /// is a strided <c>memcpy</c> into it and no command buffer exists. A non-staging texture is Private, so
        /// the bytes are staged and blitted, and M-M9 puts that blit on the device's setup command buffer instead
        /// of the whole queue submit the incumbent issued per call.
        /// </remarks>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            ArgumentNullException.ThrowIfNull(texture);
            ArgumentNullException.ThrowIfNull(data);

            // THE OWNER CHECK IS ARGUMENT VALIDATION, so it comes before the liveness return: a caller passing
            // another device's texture has made the same mistake whether or not this device is still alive.
            MetalTexture metal = MetalResourceOwnership.Require<MetalTexture>(texture, _liveness, nameof(texture));

            if (!KhaozEngineMetal.IsPlatformSupported || _liveness.IsDead) return;
            if (width == 0 || height == 0) return;

            var upload = new MetalTextureUpload(mipLevel, arrayLayer, x, y, width, height);

            if (metal.IsStaging)
            {
                lock (_rings.CpuReads.Gate)
                {
                    var layout = metal.SubresourceLayout(mipLevel, arrayLayer);
                    ulong bytes = MetalStagingLayout.RowPitch(width, metal.Format);
                    ulong xBytes = (ulong)x * MetalStagingLayout.BytesPerTexel(metal.Format);
                    for (uint row = 0; row < height; row++)
                        _rings.CpuReads.RequireWritable(metal.CpuResource,
                            layout.Offset + (y + row) * layout.RowPitch + xBytes, bytes);
                    WriteStagingTexture(metal, upload, data);
                }
            }
            else UploadThroughSetup(metal, upload, data);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// SUBRESOURCE 0, which is mip 0 of layer 0: the seam's <c>Map</c> carries no subresource, exactly as
        /// Veldrid's default overload does not. The pointer, the row pitch and the size all come from
        /// <see cref="MetalStagingLayout"/>, which reproduces the incumbent's software arithmetic byte for byte.
        /// </remarks>
        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            ArgumentNullException.ThrowIfNull(staging);

            MetalTexture metal = MetalResourceOwnership.Require<MetalTexture>(staging, _liveness, nameof(staging));
            if (!metal.IsStaging)
            {
                throw new ArgumentException(
                    "Only a GpuTextureUsage.Staging texture can be mapped on the native Metal backend. Every "
                    + "other texture is MTLStorageModePrivate and has no CPU-visible memory at all (M-M2), so "
                    + "there is no pointer to hand back. Copy into a staging texture and map that.",
                    nameof(staging));
            }

            return _cpuAccess.Map(metal, mode);
        }

        /// <inheritdoc/>
        /// <remarks>Ends the writable mapping lease. Shared memory needs no native unmap.</remarks>
        public void Unmap(IGpuTexture staging) => _rings.CpuReads.Unmap(staging);

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            ArgumentNullException.ThrowIfNull(staging);

            MetalBuffer metal = MetalResourceOwnership.Require<MetalBuffer>(staging, _liveness, nameof(staging));

            return _cpuAccess.Map(metal, mode);
        }

        /// <inheritdoc/>
        /// <remarks>Ends the writable mapping lease. See <see cref="Unmap(IGpuTexture)"/>.</remarks>
        public void Unmap(IGpuBuffer staging) => _rings.CpuReads.Unmap(staging);

        void WriteBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            MetalBuffer metal = MetalResourceOwnership.Require<MetalBuffer>(buffer, _liveness, nameof(buffer));

            if (!KhaozEngineMetal.IsPlatformSupported || _liveness.IsDead) return;

            // A RING-BACKED BUFFER TAKES THE EVERY-SEGMENT PATH (M-M5,
            // https://github.com/APKiwiOrg/KhaozEngine/issues/484). A device-level write has no frame to belong
            // to, so writing the current segment alone would leave a load-time value in one segment out of the
            // frames in flight and the other frames would bind memory nothing had ever written. It reaches every
            // segment, gated on the same completion read, deferring the ones an earlier frame is still reading
            // as pending patches rather than waiting for them, so this call never blocks whoever made it.
            if (metal.Ring is { } ring)
            {
                _rings.UpdateBuffer(ring, offsetBytes, data);
                return;
            }

            // Non-ring buffers enforce the same committed-prefix byte ranges around their shared-memory copy.
            metal.Write(offsetBytes, data);
        }

        // The batch takes the destination's HANDLE and its SHAPE rather than the wrapper, because everything it
        // needs from a MetalTexture is those two and taking them here is what keeps MetalSetupCommands free of a
        // type that can only be built on a real device.
        void UploadThroughSetup(MetalTexture destination, in MetalTextureUpload upload, byte[] data)
            => _setup.Upload(destination.Handle, destination.Shape, upload, data);

        // A staging texture is a Shared buffer, so the upload is a strided copy into it and nothing is recorded.
        // Reproduces MTLGraphicsDevice.UpdateTextureCore's else branch, which walks rows with the SOURCE pitch
        // tightly packed and the DESTINATION pitch from the software layout.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static unsafe void WriteStagingTexture(MetalTexture destination, in MetalTextureUpload upload,
            byte[] data)
        {
            // THE REGION FIRST, because the length check below only says the SOURCE is long enough. A rectangle
            // that runs past the mip's own dimensions writes past the subresource into whatever follows it, and
            // this path is a software copy with no driver behind it to notice.
            MetalStagingLayout.RequireRegionFits(destination.Shape, upload.MipLevel, upload.ArrayLayer, upload.X,
                upload.Y, upload.Width, upload.Height);

            MetalSubresourceLayout layout = destination.SubresourceLayout(upload.MipLevel, upload.ArrayLayer);

            ulong sourceRowPitch = MetalStagingLayout.RowPitch(upload.Width, destination.Format);
            ulong required = sourceRowPitch * upload.Height;

            if ((ulong)data.Length < required)
            {
                throw new ArgumentException(
                    "A native Metal staging-texture upload of " + upload.Width + " by " + upload.Height
                    + " texels in " + destination.Format + " needs " + required
                    + " tightly packed bytes and was given " + data.Length + ".", nameof(data));
            }

            uint texelBytes = MetalStagingLayout.BytesPerTexel(destination.Format);
            byte* destinationBase = (byte*)destination.StagingContents + layout.Offset;

            fixed (byte* source = data)
            {
                for (uint row = 0; row < upload.Height; row++)
                {
                    byte* from = source + (row * sourceRowPitch);

                    // THE REAL REMAINING DESTINATION, not the row pitch. Passing sourceRowPitch as
                    // destinationSizeInBytes made MemoryCopy's own overflow argument vacuous, since it then asked
                    // whether a copy of n bytes fits in n bytes and the answer was always yes. With the
                    // subresource's remaining bytes it is a second, independent check on the arithmetic the
                    // region refusal above already made.
                    ulong offsetInSubresource = ((ulong)(upload.Y + row) * layout.RowPitch)
                        + ((ulong)upload.X * texelBytes);

                    Buffer.MemoryCopy(from, destinationBase + offsetInSubresource,
                        layout.Size - offsetInSubresource, sourceRowPitch);
                }
            }
        }
    }
}
