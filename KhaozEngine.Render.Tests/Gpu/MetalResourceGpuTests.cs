using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROW 6's RESOURCES ON REAL HARDWARE: buffers, textures, samplers, the device-level uploads, the setup
    /// command buffer of M-M9 and <c>Map</c> with the read drain of M-C6.
    ///
    /// <para><b>WHAT ONLY A DEVICE CAN SETTLE, and it is the half the policy tests deliberately cannot reach.</b>
    /// Every creation decision is a pure function checked device-free in <c>MetalResourcePolicyTests</c> and
    /// <c>MetalStagingLayoutTableTests</c>. What is left is whether METAL ACCEPTS what those functions produce,
    /// which is a question about a driver: a wrong selector is an unrecognised-selector abort, a wrong
    /// <c>objc_msgSend</c> prototype is a memory corruption, and a wrong descriptor field is a nil texture. None
    /// of those is visible without hardware.</para>
    ///
    /// <para><b>TWO ROWS ASSERT BYTES AND ONE ASSERTS ACCEPTANCE, and the difference is stated rather than
    /// smoothed over.</b> The buffer round trip and the staging-texture round trip read their own writes back
    /// through <c>Map</c>, so they check the pointer, the software layout arithmetic and the strided copy against
    /// real memory. The PRIVATE-texture upload cannot be read back at all from this row: a Private texture has no
    /// CPU pointer and the reverse copy is the command list's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580). So its assertion is that the command buffer
    /// carrying the blit COMPLETED with no error, which is the same bar row 1's spike used for every shape whose
    /// value it could not observe, and it is exactly what catches the one new ABI shape this row adds. The pixel
    /// proof arrives with that row's readback.</para>
    ///
    /// <para>In <c>NativeDeviceLifecycle</c> because it creates and tears down real devices, which is the
    /// collection that keeps a second live-device backend from taking a leg from 17 minutes to 49.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalResourceGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalResourceGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// A buffer of every usage the engine creates is accepted, reports the size the CALLER asked for rather
        /// than the rounded allocation, and disposes cleanly.
        /// </summary>
        [GpuFact]
        public void Buffers_OfEveryUsageTheEngineCreates_AreAccepted()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            foreach (GpuBufferUsage usage in new[]
            {
                GpuBufferUsage.VertexBuffer, GpuBufferUsage.IndexBuffer, GpuBufferUsage.UniformBuffer,
                GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic,
                GpuBufferUsage.StructuredBufferReadOnly, GpuBufferUsage.StructuredBufferReadWrite,
                GpuBufferUsage.Staging,
            })
            {
                // 254 rather than 256, so the four-byte rounding is exercised on a real allocation rather than
                // only in the policy test: the buffer is allocated at 256 and still reports 254.
                using IGpuBuffer buffer = device.Factory.CreateBuffer(new GpuBufferDescription(254, usage));
                Assert.Equal(254u, buffer.SizeInBytes);
            }
        }

        /// <summary>
        /// M-M6's SECOND creation-time invariant against a real device, because a refusal that only exists in a
        /// policy type is a refusal a factory can forget to call. Both Veldrid backends accept this combination,
        /// so it is a documented backend-divergent creation failure and the message has to say so.
        /// </summary>
        [GpuFact]
        public void AUniformBufferThatIsAlsoStructured_IsRefusedByTheFactory()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            ArgumentException thrown = Assert.Throws<ArgumentException>(() => device.Factory.CreateBuffer(
                new GpuBufferDescription(256,
                    GpuBufferUsage.UniformBuffer | GpuBufferUsage.StructuredBufferReadOnly)));

            Assert.Contains("frame ring", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE BUFFER ROUND TRIP, with real bytes. A device-level <c>UpdateBuffer</c> is a plain <c>memcpy</c>
        /// into Shared <c>contents()</c> on this backend, and <c>Map</c> hands back that same pointer, so this
        /// checks the pointer was taken correctly at creation and that the offset arithmetic is right.
        /// </summary>
        [GpuFact]
        public void ABufferWrittenAtAnOffset_ReadsBackThroughMap()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.Staging));

            var written = new byte[16];
            for (int i = 0; i < written.Length; i++) written[i] = (byte)(i + 1);

            device.UpdateBuffer(buffer, 8, written);

            MappedData mapped = device.Map(buffer, GpuMapMode.Read);
            try
            {
                Assert.Equal(64u, mapped.SizeInBytes);
                Assert.NotEqual(IntPtr.Zero, mapped.Data);

                byte[] read = ReadBytes(mapped.Data, 8, written.Length);
                Assert.Equal(written, read);
            }
            finally
            {
                device.Unmap(buffer);
            }
        }

        /// <summary>A write past the end is refused by name rather than corrupting whatever the driver put after
        /// the allocation, which is what the incumbent's unguarded copy does.</summary>
        [GpuFact]
        public void ABufferWritePastTheEnd_IsRefused()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(16, GpuBufferUsage.VertexBuffer));

            Assert.Throws<ArgumentOutOfRangeException>(() => device.UpdateBuffer(buffer, 8, new byte[16]));
        }

        /// <summary>
        /// Every texture shape the engine creates is accepted by a real device, which is what checks the
        /// descriptor's field writes and the format map against the driver rather than against a table. The
        /// depth format is <see cref="GpuPixelFormat.D32FloatS8UInt"/> and not
        /// <see cref="GpuPixelFormat.D24UNormS8UInt"/> deliberately: Apple silicon has no
        /// <c>Depth24Unorm_Stencil8</c> at all, which is a fact about the device and not about the map.
        /// </summary>
        [GpuFact]
        public void Textures_OfEveryShapeTheEngineCreates_AreAccepted()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            GpuTextureDescription[] shapes =
            [
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.B8G8R8A8UNorm,
                    GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R16G16B16A16Float,
                    GpuTextureUsage.RenderTarget),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R16G16Float, GpuTextureUsage.RenderTarget),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R32Float,
                    GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget),
                // The SAME seam format as a DEPTH target, which is the one row of the format map whose answer
                // depends on the usage rather than on the format.
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R32Float, GpuTextureUsage.DepthStencil),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.D32FloatS8UInt,
                    GpuTextureUsage.DepthStencil),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled | GpuTextureUsage.Storage),
                new GpuTextureDescription(64, 64, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 7),
                GpuTextureDescription.Texture2DArray(32, 32, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled, arrayLayers: 4, mipLevels: 3),
                new GpuTextureDescription(32, 32, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap, arrayLayers: 1),
                GpuTextureDescription.Texture2D(64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging),
            ];

            foreach (GpuTextureDescription shape in shapes)
            {
                using IGpuTexture texture = device.Factory.CreateTexture(shape);

                Assert.Equal(shape.Width, texture.Width);
                Assert.Equal(shape.Height, texture.Height);
                Assert.Equal(shape.MipLevels, texture.MipLevels);
                Assert.Equal(shape.Format, texture.Format);
            }
        }

        /// <summary>
        /// THE STAGING ROUND TRIP, which is the arithmetic the goldens depend on, checked against real memory
        /// rather than against the table. It writes a sub-rectangle at a non-zero origin so the destination row
        /// pitch and the column offset both have to be right, and reads back through <c>Map</c>, whose reported
        /// row pitch is what a golden de-strides with.
        /// </summary>
        [GpuFact]
        public void AStagingTexture_TakesAStridedUpload_AndReadsBackThroughMap()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(8, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

            // A 2x2 rectangle at (1, 1), tightly packed: four texels of four bytes.
            var payload = new byte[2 * 2 * 4];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0x10 + i);

            device.UpdateTexture(staging, payload, 1, 1, 2, 2);

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                Assert.Equal(32u, mapped.RowPitch);
                Assert.Equal(32u * 4, mapped.SizeInBytes);

                // Row 1 of the destination, four bytes in, is the payload's first row. Row 2 is its second.
                Assert.Equal(payload[..8], ReadBytes(mapped.Data, (32 * 1) + 4, 8));
                Assert.Equal(payload[8..], ReadBytes(mapped.Data, (32 * 2) + 4, 8));

                // And the texel to the left of the written rectangle is untouched, which is what says the
                // column offset was applied rather than the row simply starting at zero.
                Assert.Equal(new byte[4], ReadBytes(mapped.Data, 32 * 1, 4));
            }
            finally
            {
                device.Unmap(staging);
            }
        }

        /// <summary>
        /// A MIPPED, LAYERED staging texture maps SUBRESOURCE 0 and reports mip 0's pitch and size, which is the
        /// incumbent's own default overload behaviour and what every golden readback uses.
        /// </summary>
        [GpuFact]
        public void AMippedLayeredStagingTexture_MapsSubresourceZero()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuTexture staging = device.Factory.CreateTexture(
                new GpuTextureDescription(16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging,
                    mipLevels: 3, arrayLayers: 2));

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                Assert.Equal(64u, mapped.RowPitch);
                Assert.Equal(64u * 16, mapped.SizeInBytes);
            }
            finally
            {
                device.Unmap(staging);
            }
        }

        /// <summary>Mapping a texture that is not a staging texture is refused with the reason, because a Private
        /// texture has no CPU-visible memory at all and a null pointer would be discovered somewhere else.</summary>
        [GpuFact]
        public void MappingANonStagingTexture_IsRefused()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuTexture texture = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));

            Assert.Throws<ArgumentException>(() => device.Map(texture, GpuMapMode.Read));
        }

        /// <summary>
        /// M-M9's SETUP COMMAND BUFFER, and the one new ABI shape this row adds.
        ///
        /// <para><b>THE ASSERTION IS ACCEPTANCE RATHER THAN BYTES, and the class summary says why.</b> The
        /// destination is Private and the reverse copy belongs to the draw-and-copy row, so what is checked here
        /// is that a real device took the eleven-argument
        /// <c>copyFromBuffer:...toTexture:destinationSlice:destinationLevel:destinationOrigin:</c> whose last
        /// three arguments cross on the stack, and that the command buffer carrying it completed with no error.
        /// A wrong prototype presents as a crash or a validation failure rather than as a wrong pixel.</para>
        ///
        /// <para><b>AND THE RATIO IS THE POINT OF THE DECISION.</b> Eight uploads produce ONE committed batch,
        /// where the incumbent issues one whole queue submit per call. The counters are read before and after the
        /// flush so the claim is a measurement rather than a description.</para>
        /// </summary>
        [GpuFact]
        public void EightDeviceLevelTextureUploads_ShareOneSetupCommandBuffer()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var metal = (MetalGpuDevice)device;

            using IGpuTexture target = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(16, 16, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));

            var payload = new byte[4 * 4 * 4];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

            for (uint i = 0; i < 8; i++) device.UpdateTexture(target, payload, i, 0, 4, 4);

            Assert.Equal(8, metal.Setup.AppendCount);
            Assert.Equal(0, metal.Setup.FlushCount);
            Assert.True(metal.Setup.HasPendingWork);

            // WaitForIdle flushes and then drains, which is the read-path half of M-M9 through the explicit
            // drain rather than through Map.
            device.WaitForIdle();

            Assert.Equal(1, metal.Setup.FlushCount);
            Assert.False(metal.Setup.HasPendingWork);

            _output.WriteLine($"{metal.Setup.AppendCount} uploads in {metal.Setup.FlushCount} committed batches");
        }

        /// <summary>
        /// A PRIVATE-texture upload followed immediately by a <c>Map</c> of an unrelated staging texture must not
        /// hang or fail, which is the read-path flush of M-M9 exercised through <c>Map</c> rather than through
        /// <c>WaitForIdle</c>. The claim being checked is that <c>Map</c> commits the pending batch BEFORE it
        /// drains, because a drain that ran first would wait for everything except the upload just made.
        /// </summary>
        [GpuFact]
        public void MapFlushesThePendingSetupBatchBeforeItDrains()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var metal = (MetalGpuDevice)device;

            using IGpuTexture target = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

            device.UpdateTexture(target, new byte[8 * 8 * 4], 0, 0, 8, 8);
            Assert.True(metal.Setup.HasPendingWork);

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            device.Unmap(staging);

            Assert.NotEqual(IntPtr.Zero, mapped.Data);
            Assert.False(metal.Setup.HasPendingWork);
            Assert.Equal(1, metal.Setup.FlushCount);
        }

        /// <summary>
        /// The shared sampler pair exists on a real device and is the SAME object each time it is asked for, which
        /// is what says the device owns it rather than creating one per read. Creating a sampler per property
        /// read would leak one per bind under a frame loop.
        /// </summary>
        [GpuFact]
        public void TheSharedSamplerPair_IsCreatedOnceAndOwnedByTheDevice()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            Assert.NotNull(device.PointSampler);
            Assert.NotNull(device.LinearSampler);
            Assert.Same(device.PointSampler, device.PointSampler);
            Assert.Same(device.LinearSampler, device.LinearSampler);
            Assert.NotSame(device.PointSampler, device.LinearSampler);
        }

        /// <summary>Every sampler description the engine builds is accepted, including the anisotropic one whose
        /// maximum anisotropy rides a separate field from its filters.</summary>
        [GpuFact]
        public void Samplers_OfEveryDescriptionTheEngineBuilds_AreAccepted()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            GpuSamplerDescription[] descriptions =
            [
                GpuSamplerDescription.Point,
                GpuSamplerDescription.Linear,
                new(GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                    GpuSamplerAddress.Wrap, maximumAnisotropy: 16),
                new(GpuSamplerFilter.MinLinearMagLinearMipLinear, GpuSamplerAddress.Border,
                    GpuSamplerAddress.Mirror, GpuSamplerAddress.Clamp),
                // A non-zero LOD bias, which Metal's sampler has no field for at all. It must be accepted and
                // dropped rather than refused: SamplerLodBias is false on this backend and on the incumbent.
                new(GpuSamplerFilter.MinLinearMagLinearMipLinear, mipLodBias: 2),
            ];

            foreach (GpuSamplerDescription description in descriptions)
            {
                using IGpuSampler sampler = device.Factory.CreateSampler(description);
                Assert.NotNull(sampler);
            }
        }

        /// <summary>
        /// A fence comes from the factory, is unsignalled before anything submits, and survives a reset. The
        /// SIGNALLING half needs a submit, which is the command-list row's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), and <c>MetalTimelineGpuTests</c> already drives
        /// the timeline directly.
        /// </summary>
        [GpuFact]
        public void AFenceComesFromTheFactory_AndIsUnsignalledBeforeAnythingSubmits()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            using IGpuFence fence = device.Factory.CreateFence();

            Assert.False(fence.Signaled);
            fence.Reset();
            Assert.False(fence.Signaled);
        }

        /// <summary>
        /// MANY RESOURCES CREATED AND DESTROYED IN SEQUENCE, because a leaked <c>MTLBuffer</c> or a released
        /// descriptor that was never released shows up as a slow crawl rather than as a failure. This is the
        /// cheapest place to notice an ownership mistake in the +1 handling.
        /// </summary>
        [GpuFact]
        public void ManyResourcesInSequence_CreateAndTearDownCleanly()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();

            for (int i = 0; i < 64; i++)
            {
                using IGpuBuffer buffer = device.Factory.CreateBuffer(
                    new GpuBufferDescription(1024, GpuBufferUsage.VertexBuffer));
                using IGpuTexture texture = device.Factory.CreateTexture(
                    GpuTextureDescription.Texture2D(32, 32, GpuPixelFormat.R8G8B8A8UNorm,
                        GpuTextureUsage.Sampled));
                using IGpuSampler sampler = device.Factory.CreateSampler(GpuSamplerDescription.Linear);

                Assert.Equal(1024u, buffer.SizeInBytes);
            }
        }

        /// <summary>
        /// Disposing a resource AFTER its device is not a crash, which is the teardown order every consumer hits
        /// and the reason every wrapper carries the liveness token (M-F6).
        /// </summary>
        [GpuFact]
        public void AResourceDisposedAfterItsDevice_IsASafeNoOp()
        {
            if (!Available()) return;

            IGpuDevice device = CreateHeadless();
            IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
            IGpuTexture texture = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            IGpuSampler sampler = device.Factory.CreateSampler(GpuSamplerDescription.Point);

            device.Dispose();

            buffer.Dispose();
            texture.Dispose();
            sampler.Dispose();
        }

        /// <summary>The members other rows own still name their row, now that the factory itself is live: a
        /// reader who hits one needs to know whether the backend is unfinished or their machine is wrong.</summary>
        [GpuFact]
        public void TheFactorysUnbuiltMembers_NameTheirRows()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            Assert.Contains("573", Refusal(() => factory.CreateCommandList()), StringComparison.Ordinal);
            Assert.Contains("575", Refusal(() => factory.CreateShadersFromSpirv("", "")),
                StringComparison.Ordinal);
            Assert.Contains("576", Refusal(() => factory.CreateResourceLayout(default)),
                StringComparison.Ordinal);
            Assert.Contains("577", Refusal(() => factory.CreateGraphicsPipeline(default)),
                StringComparison.Ordinal);
            Assert.Contains("578", Refusal(() => factory.CreateFramebuffer(null)), StringComparison.Ordinal);

            string commandList = Refusal(() => factory.CreateCommandList());
            _output.WriteLine(commandList);
            Assert.Contains("Buffers, textures, samplers and fences ARE live", commandList,
                StringComparison.Ordinal);
        }

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        static string Refusal(Func<object> call) => Assert.ThrowsAny<Exception>(() => call()).Message;

        static byte[] ReadBytes(IntPtr basePointer, int offset, int count)
        {
            var bytes = new byte[count];
            Marshal.Copy(basePointer + offset, bytes, 0, count);
            return bytes;
        }

        // [SupportedOSPlatformGuard] rather than an inline check at every call site, the same mechanism
        // MetalDeviceLifecycleTests uses: the first thing this asks is that guard, so a true answer really does
        // imply macOS. Dormant off macOS rather than skipped, which is phase 3's row-19 lesson.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create resources on.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}
