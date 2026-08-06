using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STAGING <c>Map</c> AND <c>Unmap</c> PAIR, minus the device: what a mapping answers with, the two
    /// refusals a caller can earn, and the row-by-row write a device-level <c>UpdateTexture</c> on a STAGING
    /// texture is.
    ///
    /// <para><b>THERE IS NO <c>vkMapMemory</c> ANYWHERE ON THIS PATH.</b> Host-visible chunks are mapped once at
    /// chunk creation and never unmapped (V-M3), so a map here is a pointer plus an offset. Anyone porting the
    /// Direct3D 11 backend's map lifecycle across is porting a workaround for a restriction Vulkan does not
    /// have.</para>
    /// </summary>
    public sealed class VulkanStagingMapTests
    {
        /// <summary>
        /// A TEXTURE MAPPING ANSWERS WITH THE SOFTWARE LAYOUT'S OWN NUMBERS (V-C7): the base plus the subresource
        /// offset, the row pitch every golden de-strides with, and the subresource's size. Not the buffer's whole
        /// size, which for a mipped or layered staging texture would let a caller read into the next subresource.
        /// </summary>
        [Fact]
        public void ATextureMapping_CarriesTheSubresourcesOwnPitchAndSize()
        {
            var shape = new VulkanStagingShape(16, 16, 5, 2, GpuPixelFormat.R8G8B8A8UNorm);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, 1, 1);

            MappedData mapped = VulkanStagingMaps.ForTexture(0x1000, layout);

            Assert.Equal((nint)(0x1000 + (long)layout.Offset), mapped.Data);
            Assert.Equal(32u, mapped.RowPitch);
            Assert.Equal(256u, mapped.SizeInBytes);
        }

        /// <summary>
        /// A BUFFER'S ROW PITCH IS ITS SIZE, which is what the seam already documents ("RowPitch is meaningless for
        /// a buffer, it equals the size"). It is answered that way rather than as zero because
        /// <c>GpuReadback.ReadBuffer</c> and the Veldrid path both read it, and a zero would turn a stride into a
        /// division by nothing.
        /// </summary>
        [Fact]
        public void ABufferMapping_ReportsItsSizeAsItsRowPitch()
        {
            MappedData mapped = VulkanStagingMaps.ForBuffer(0x2000, 4096);

            Assert.Equal((nint)0x2000, mapped.Data);
            Assert.Equal(4096u, mapped.RowPitch);
            Assert.Equal(4096u, mapped.SizeInBytes);
        }

        /// <summary>
        /// MAPPING TWICE IS REFUSED BY NAME. Vulkan reports nothing at all here: the second map would hand back the
        /// same pointer and the second reader would see whatever the first left behind, so a readback that quietly
        /// returns the previous frame's pixels is the shape this takes in the field.
        /// </summary>
        [Fact]
        public void MappingTwice_IsRefused()
        {
            var maps = new VulkanStagingMaps();
            var resource = new object();

            maps.Open(resource, GpuMapMode.Read);

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => maps.Open(resource, GpuMapMode.Write));

            Assert.Contains("already mapped", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, maps.OpenCount);
        }

        /// <summary>
        /// UNMAPPING SOMETHING THAT WAS NEVER MAPPED IS REFUSED, for the same reason: there is no
        /// <c>vkUnmapMemory</c> on this path at all, so the mistake would be entirely silent.
        /// </summary>
        [Fact]
        public void UnmappingWhatWasNeverMapped_IsRefused()
        {
            var maps = new VulkanStagingMaps();

            Assert.Throws<InvalidOperationException>(() => maps.Close(new object()));
        }

        /// <summary>
        /// CLOSING ANSWERS WITH THE MODE THE MAP WAS TAKEN IN, which is how the unmap knows whether host writes
        /// have to be made visible to the device. A read map needs no flush, a write map does, and on a coherent
        /// memory type both are free.
        /// </summary>
        [Fact]
        public void Closing_AnswersWithTheModeTheMapWasTakenIn()
        {
            var maps = new VulkanStagingMaps();
            var resource = new object();

            maps.Open(resource, GpuMapMode.ReadWrite);

            Assert.Equal(GpuMapMode.ReadWrite, maps.Close(resource));
            Assert.Equal(0, maps.OpenCount);
        }

        /// <summary>
        /// THE READ AND WRITE PREDICATES COVER ALL THREE MODES, which is what decides the V-C8 drain on the way in
        /// and the flush on the way out. <see cref="GpuMapMode.ReadWrite"/> is both, and getting it into only one
        /// of the two sets is the shape of the mistake here.
        /// </summary>
        [Fact]
        public void TheReadAndWritePredicates_CoverAllThreeModes()
        {
            Assert.True(VulkanStagingMaps.Reads(GpuMapMode.Read));
            Assert.False(VulkanStagingMaps.Reads(GpuMapMode.Write));
            Assert.True(VulkanStagingMaps.Reads(GpuMapMode.ReadWrite));

            Assert.False(VulkanStagingMaps.Writes(GpuMapMode.Read));
            Assert.True(VulkanStagingMaps.Writes(GpuMapMode.Write));
            Assert.True(VulkanStagingMaps.Writes(GpuMapMode.ReadWrite));
        }

        /// <summary>
        /// FORGETTING DROPS EVERY MAPPING WITHOUT CLOSING IT, which is what device teardown does: after the flip
        /// the memory behind one does not exist, so a later Unmap is a call about a resource with nothing under it.
        /// </summary>
        [Fact]
        public void Forgetting_DropsEveryMapping()
        {
            var maps = new VulkanStagingMaps();
            maps.Open(new object(), GpuMapMode.Read);
            maps.Open(new object(), GpuMapMode.Read);

            Assert.Equal(2, maps.Forget());
            Assert.Equal(0, maps.OpenCount);
        }

        /// <summary>
        /// TWO RESOURCES ARE TRACKED SEPARATELY EVEN IF THEY COMPARE EQUAL, because the registry keys on REFERENCE
        /// identity. A resource type that overrode equality (a record, which several types in this backend are)
        /// would otherwise make two live staging buffers look like one mapping.
        /// </summary>
        [Fact]
        public void TwoResourcesThatCompareEqual_AreTrackedSeparately()
        {
            var maps = new VulkanStagingMaps();
            var first = new EqualByValue();
            var second = new EqualByValue();

            Assert.Equal(first, second);

            maps.Open(first, GpuMapMode.Read);
            maps.Open(second, GpuMapMode.Read);

            Assert.Equal(2, maps.OpenCount);
        }

        /// <summary>
        /// THE STAGING-TEXTURE WRITE PLACES EACH SOURCE ROW AT THE DESTINATION'S OWN STRIDE, which is the whole of
        /// what a device-level <c>UpdateTexture</c> on a staging texture is: the source rows are tightly packed and
        /// the destination rows are not. A single memcpy would be right only when the region is the full width with
        /// no padding, and getting it wrong writes each row one stride further along, which is the diagonal smear a
        /// reader recognises instantly and a test has to actually check for.
        /// </summary>
        [Fact]
        public void TheStagingWrite_PlacesEachRowAtTheDestinationStride()
        {
            var shape = new VulkanStagingShape(4, 4, 1, 1, GpuPixelFormat.R8UNorm);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, 0, 0);

            var destination = new byte[16];
            GCHandle pin = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                // A 2x2 rectangle of 1s at (1, 1) of a 4x4 single-channel surface.
                VulkanStagingMaps.WriteRegion(pin.AddrOfPinnedObject(), layout, bytesPerTexel: 1, x: 1, y: 1,
                    width: 2, height: 2, data: [1, 1, 1, 1]);

                Assert.Equal(
                    new byte[]
                    {
                        0, 0, 0, 0,
                        0, 1, 1, 0,
                        0, 1, 1, 0,
                        0, 0, 0, 0,
                    },
                    destination);
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// THE WRITE LANDS AT THE SUBRESOURCE'S OWN OFFSET, so an upload to mip 1 does not overwrite mip 0. The
        /// offset comes from the same arithmetic the map does, which is what keeps the two agreeing.
        /// </summary>
        [Fact]
        public void TheStagingWrite_LandsAtTheSubresourcesOffset()
        {
            var shape = new VulkanStagingShape(4, 4, 2, 1, GpuPixelFormat.R8UNorm);
            VulkanSubresourceLayout mip1 = VulkanStagingLayout.For(shape, 1, 0);

            Assert.Equal(16UL, mip1.Offset);

            var destination = new byte[20];
            GCHandle pin = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                VulkanStagingMaps.WriteRegion(pin.AddrOfPinnedObject(), mip1, 1, 0, 0, 2, 2, [7, 7, 7, 7]);

                // Mip 0's sixteen bytes are untouched and mip 1's four carry the payload.
                for (int i = 0; i < 16; i++) Assert.Equal(0, destination[i]);
                Assert.Equal(new byte[] { 7, 7, 7, 7 }, destination[16..20]);
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// A SHORT PAYLOAD AND A RECTANGLE THAT LEAVES THE SUBRESOURCE ARE BOTH REFUSED, rather than read past or
        /// written into the next subresource's bytes.
        /// </summary>
        [Fact]
        public void AShortPayloadOrAnOversizedRectangle_IsRefused()
        {
            var shape = new VulkanStagingShape(4, 4, 1, 1, GpuPixelFormat.R8UNorm);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, 0, 0);

            var destination = new byte[16];
            GCHandle pin = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                nint address = pin.AddrOfPinnedObject();

                Assert.Throws<ArgumentException>(() =>
                    VulkanStagingMaps.WriteRegion(address, layout, 1, 0, 0, 2, 2, new byte[3]));

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    VulkanStagingMaps.WriteRegion(address, layout, 1, 0, 0, 8, 2, new byte[16]));

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    VulkanStagingMaps.WriteRegion(address, layout, 1, 0, 3, 4, 2, new byte[8]));
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>A write through a null mapping is refused rather than dereferenced, which cannot happen through
        /// the readback ladder and is the one line that would crash the process rather than fail a test.</summary>
        [Fact]
        public void AWriteThroughANullMapping_IsRefused()
        {
            var shape = new VulkanStagingShape(4, 4, 1, 1, GpuPixelFormat.R8UNorm);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(shape, 0, 0);

            Assert.Throws<InvalidOperationException>(() =>
                VulkanStagingMaps.WriteRegion(0, layout, 1, 0, 0, 1, 1, new byte[1]));
        }

        // Two of these compare equal and are not the same object, which is what the reference-identity registry
        // has to survive.
        sealed record EqualByValue;
    }
}
