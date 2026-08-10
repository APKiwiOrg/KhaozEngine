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

        /// <summary>The device's completion timeline (M-F1). Every fence sits on it, and the command-list row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573) is what starts encoding signals into it at
        /// submit and registering the values a commit accepted.</summary>
        internal MetalTimeline Timeline => _timeline;

        /// <summary>
        /// The device-owned setup command buffer (M-M9). Exposed so the command-list row can flush it at the top
        /// of <c>Submit</c>, which is the third of its three flush sites: <see cref="Map(IGpuTexture,GpuMapMode)"/>,
        /// <see cref="Map(IGpuBuffer,GpuMapMode)"/> and <see cref="WaitForIdle"/> are the two this row owns.
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
        /// of the whole queue submit the incumbent issues per call.
        /// </remarks>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            ArgumentNullException.ThrowIfNull(texture);
            ArgumentNullException.ThrowIfNull(data);

            if (!KhaozEngineMetal.IsPlatformSupported || _liveness.IsDead) return;
            if (width == 0 || height == 0) return;

            var metal = (MetalTexture)texture;
            var upload = new MetalTextureUpload(mipLevel, arrayLayer, x, y, width, height);

            if (metal.IsStaging) WriteStagingTexture(metal, upload, data);
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

            var metal = (MetalTexture)staging;
            if (!metal.IsStaging)
            {
                throw new ArgumentException(
                    "Only a GpuTextureUsage.Staging texture can be mapped on the native Metal backend. Every "
                    + "other texture is MTLStorageModePrivate and has no CPU-visible memory at all (M-M2), so "
                    + "there is no pointer to hand back. Copy into a staging texture and map that.",
                    nameof(staging));
            }

            DrainForRead(mode);
            return metal.Mapped(subresource: 0);
        }

        /// <inheritdoc/>
        /// <remarks>A NO-OP, as it is in the incumbent: a Shared buffer's <c>contents()</c> pointer is valid for
        /// the buffer's life and there is nothing to unmap.</remarks>
        public void Unmap(IGpuTexture staging) { }

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            ArgumentNullException.ThrowIfNull(staging);

            DrainForRead(mode);
            return ((MetalBuffer)staging).Mapped();
        }

        /// <inheritdoc/>
        /// <remarks>A no-op. See <see cref="Unmap(IGpuTexture)"/>.</remarks>
        public void Unmap(IGpuBuffer staging) { }

        /// <summary>
        /// M-C6's READ DRAIN, and the setup flush that has to happen before it.
        ///
        /// <para><b>THE INCUMBENT DOES NOT WAIT AT ALL, and that is the defect this closes.</b>
        /// <c>MTLGraphicsDevice.MapCore</c> hands back <c>contents()</c> immediately. It works today only because
        /// every engine caller drains first (<c>GpuReadback</c> submits and drains before mapping), so the seam's
        /// guarantee rests on a CALLER CONVENTION rather than on the backend. Getting it wrong returns a pointer
        /// to bytes the blit has not written yet, which reads as an intermittently wrong golden, and an
        /// intermittently wrong golden on a real device is the worst failure shape a five-legged blocking matrix
        /// has.</para>
        ///
        /// <para><b>THE FLUSH COMES FIRST AND IT IS THE OTHER HALF OF THE SAME GUARANTEE (M-M9).</b> A texture
        /// uploaded through the setup buffer and then read back must see the uploaded bytes, so the batch is
        /// committed before the drain rather than after it. A drain that ran first would wait for everything
        /// EXCEPT the upload the caller just made.</para>
        ///
        /// <para><b>A WRITE MAPPING DOES NOT DRAIN.</b> The seam's own reason for a wait is that the bytes the
        /// caller is about to READ may not have arrived, and a caller mapping for
        /// <see cref="GpuMapMode.Write"/> is the producer rather than the consumer. The incumbent does not wait
        /// in either mode, so this is strictly narrower than "always wait" and strictly safer than what ships.</para>
        /// </summary>
        void DrainForRead(GpuMapMode mode)
        {
            _setup.Flush();

            if (mode == GpuMapMode.Write) return;

            // WaitForIdle rather than the timeline directly: it is the device's ONE drain point, it is already a
            // real drain through an empty command buffer on a queue that executes in enqueue order, and it is
            // what the command-list row swaps onto waitUntilSignaledValue:timeoutMS: when there is a submitted
            // value to wait for. Routing through it means the read drain follows that change for free.
            WaitForIdle();
        }

        void WriteBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (!KhaozEngineMetal.IsPlatformSupported || _liveness.IsDead) return;

            // A ring-backed buffer's device-level write is #484's every-segment rule and belongs to the ring row
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/574). Until then a uniform buffer is a plain
            // Shared buffer of the requested size, so one write reaches the only bytes there are, which is the
            // incumbent's behaviour exactly. The predicate is read here rather than assumed away so that row has
            // a named place to branch.
            ((MetalBuffer)buffer).Write(offsetBytes, data);
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
                    byte* to = destinationBase
                        + ((upload.Y + row) * layout.RowPitch)
                        + ((ulong)upload.X * texelBytes);

                    Buffer.MemoryCopy(from, to, sourceRowPitch, sourceRowPitch);
                }
            }
        }
    }
}
