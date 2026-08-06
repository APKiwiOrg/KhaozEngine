using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT IS CURRENTLY MAPPED, AND THE ARITHMETIC A MAP ANSWERS WITH, with no device under either. The seam's
    /// <c>IGpuDevice.Map</c> and <c>Unmap</c> pair is the readback path (<c>GpuReadback</c> wraps the whole
    /// staging-copy-map-unmap sequence), and everything about it that can be wrong without a GPU lives here.
    ///
    /// <para><b>THERE IS NO <c>vkMapMemory</c> AND NO <c>vkUnmapMemory</c> ANYWHERE ON THIS PATH, WHICH IS THE
    /// HEADLINE.</b> Host-visible chunks are mapped once at chunk creation and never unmapped (V-M3), so a
    /// <c>Map</c> here is a pointer plus an offset and an <c>Unmap</c> is bookkeeping plus, on a non-coherent
    /// memory type, a flush. Anyone porting the Direct3D 11 backend's map lifecycle across is porting a workaround
    /// for a restriction Vulkan does not have.</para>
    ///
    /// <para><b>THE TWO REFUSALS ARE WORTH HAVING RATHER THAN LEAVING TO THE DRIVER.</b> Mapping an
    /// already-mapped resource and unmapping one that was never mapped are both ordering mistakes in the CALLER,
    /// and Vulkan reports neither: the second map would simply hand back the same pointer and the stray unmap would
    /// do nothing. A readback that quietly returns the previous frame's pixels is the shape they take in the field.
    /// Refusing by name makes each one a named exception on every platform and a plain <c>[Fact]</c> on any
    /// machine.</para>
    ///
    /// <para><b>A TEXTURE'S ROW PITCH AND SIZE COME FROM THE SOFTWARE LAYOUT (V-C7)</b>, which is
    /// <see cref="VulkanStagingLayout"/>'s, so the numbers a golden de-strides with are the incumbent's own
    /// arithmetic rather than a driver's answer. A BUFFER'S ROW PITCH IS ITS SIZE, which is what the seam already
    /// documents, and it is answered that way rather than as zero because <c>GpuReadback.ReadBuffer</c> and the
    /// Veldrid path both read it and a zero would turn a stride into a division by nothing.</para>
    ///
    /// <para><b>SUBRESOURCE 0, ALWAYS.</b> The seam's <c>Map</c> takes a resource and a mode and nothing else, and
    /// the Veldrid path it mirrors defaults the subresource to 0 at the same call. The rest of the layout table
    /// exists because a COPY writes every subresource, not because a map can name one.</para>
    ///
    /// <para><b>ONE PER DEVICE, AND NOT THREAD-SAFE BY ITSELF.</b> The device's <c>Map</c> and <c>Unmap</c> take
    /// the submit lock for the duration of the call, exactly as 11.4 says, so two threads mapping two staging
    /// resources are serialised against each other and against a submit.</para>
    /// </summary>
    internal sealed class VulkanStagingMaps
    {
        readonly Dictionary<object, GpuMapMode> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>The one subresource the seam can name. See the class note.</summary>
        internal const uint Subresource = 0;

        /// <summary>How many mappings are open right now. Zero at every frame boundary in a correct consumer, and
        /// the number a leak shows up in.</summary>
        internal int OpenCount => _open.Count;

        /// <summary>Whether <paramref name="resource"/> is mapped.</summary>
        internal bool IsMapped(object resource) => _open.ContainsKey(resource);

        /// <summary>
        /// Record a mapping, refusing a second one of the same resource.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is already mapped.</exception>
        internal void Open(object resource, GpuMapMode mode)
        {
            ArgumentNullException.ThrowIfNull(resource);

            if (_open.TryGetValue(resource, out GpuMapMode existing))
            {
                throw new InvalidOperationException(
                    $"That native Vulkan staging resource is already mapped for {existing}, so a second map for "
                    + $"{mode} is a caller that lost track of one. Vulkan would hand back the same pointer and "
                    + "report nothing at all, so the second reader would see whatever the first left behind. "
                    + "Unmap the first mapping before taking another: a readback is Map, read, Unmap, and holding "
                    + "one open across a frame is what this refusal is really about.");
            }

            _open.Add(resource, mode);
        }

        /// <summary>
        /// Close a mapping and answer which mode it was taken in, so the caller knows whether host writes have to
        /// be made visible to the device.
        /// </summary>
        /// <exception cref="InvalidOperationException">It was never mapped.</exception>
        internal GpuMapMode Close(object resource)
        {
            ArgumentNullException.ThrowIfNull(resource);

            if (!_open.Remove(resource, out GpuMapMode mode))
            {
                throw new InvalidOperationException(
                    "Unmap was called on a native Vulkan staging resource that is not mapped. Vulkan has nothing "
                    + "to say about that (there is no vkUnmapMemory on this path at all: host-visible memory is "
                    + "mapped once at chunk creation and never unmapped, V-M3), so the mistake would be silent. "
                    + "Either the Map was never made or an earlier Unmap already closed it.");
            }

            return mode;
        }

        /// <summary>
        /// Drop every mapping WITHOUT closing it, for a device that is dead or being torn down. After the device is
        /// gone the mappings do not exist, and a later Unmap against one would be a caller error about a resource
        /// that has no memory behind it any more.
        /// </summary>
        /// <returns>How many were dropped, for the report line.</returns>
        internal int Forget()
        {
            int count = _open.Count;
            _open.Clear();
            return count;
        }

        /// <summary>Whether <paramref name="mode"/> reads, and therefore whether the map has to WAIT on the
        /// timeline first (V-C8) and invalidate on a non-coherent type.</summary>
        internal static bool Reads(GpuMapMode mode) => mode is GpuMapMode.Read or GpuMapMode.ReadWrite;

        /// <summary>Whether <paramref name="mode"/> writes, and therefore whether the unmap has to flush on a
        /// non-coherent type.</summary>
        internal static bool Writes(GpuMapMode mode) => mode is GpuMapMode.Write or GpuMapMode.ReadWrite;

        /// <summary>A texture's mapping, from its software subresource layout.</summary>
        internal static MappedData ForTexture(nint mappedBase, in VulkanSubresourceLayout layout)
            => new(mappedBase + (nint)layout.Offset, (uint)layout.RowPitch, (uint)layout.Size);

        /// <summary>A buffer's mapping. The row pitch IS the size: see the class note.</summary>
        internal static MappedData ForBuffer(nint mappedBase, uint sizeInBytes)
            => new(mappedBase, sizeInBytes, sizeInBytes);

        /// <summary>
        /// WRITE A TIGHTLY PACKED RECTANGLE into a staging texture's own mapping, row by row at the subresource's
        /// stride. What a device-level <c>UpdateTexture</c> on a STAGING texture is: it has no image to copy into
        /// and its memory is host-visible by construction, so the bytes go straight in.
        /// <para>
        /// THE SOURCE ROWS ARE PACKED AND THE DESTINATION ROWS ARE NOT, which is the whole of the loop. A single
        /// memcpy would be right only when the region is the full width of the subresource and the pitch has no
        /// padding, and getting that wrong writes each row one stride further along than it belongs, which is the
        /// diagonal-smear failure a reader recognises instantly and a test has to actually check for.
        /// </para>
        /// </summary>
        /// <param name="mappedBase">The staging texture's mapped first byte.</param>
        /// <param name="layout">The destination subresource's software layout.</param>
        /// <param name="bytesPerTexel">The format's texel size, which turns the column offset into bytes.</param>
        /// <param name="x">Left edge of the written rectangle.</param>
        /// <param name="y">Top edge.</param>
        /// <param name="width">Rectangle width in texels.</param>
        /// <param name="height">Rectangle height in texels.</param>
        /// <param name="data">The packed payload, at least <c>width * bytesPerTexel * height</c> bytes.</param>
        internal static unsafe void WriteRegion(nint mappedBase, in VulkanSubresourceLayout layout,
            uint bytesPerTexel, uint x, uint y, uint width, uint height, ReadOnlySpan<byte> data)
        {
            if (mappedBase == 0)
            {
                throw new InvalidOperationException(
                    "A native Vulkan staging texture has no mapping, which cannot happen through the readback "
                    + "ladder: every one of its rungs is host-visible and a host-visible chunk is mapped once at "
                    + "creation and never unmapped (V-M3).");
            }

            ulong rowBytes = (ulong)width * bytesPerTexel;
            ulong required = rowBytes * height;

            if ((ulong)data.Length < required)
            {
                throw new ArgumentException(
                    "A native Vulkan staging texture write of " + width + " by " + height + " texels needs "
                    + required + " tightly packed bytes and was given " + data.Length + ".", nameof(data));
            }

            if (rowBytes > layout.RowPitch || (ulong)(y + height) * layout.RowPitch > layout.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width,
                    "That rectangle runs past the end of the native Vulkan staging subresource it is being "
                    + "written into, which would spill into the next subresource's bytes.");
            }

            byte* destination = (byte*)mappedBase + layout.Offset + ((ulong)x * bytesPerTexel);

            for (uint row = 0; row < height; row++)
            {
                ReadOnlySpan<byte> source = data.Slice((int)(row * rowBytes), (int)rowBytes);
                source.CopyTo(new Span<byte>(destination + ((y + row) * layout.RowPitch), (int)rowBytes));
            }
        }
    }
}
