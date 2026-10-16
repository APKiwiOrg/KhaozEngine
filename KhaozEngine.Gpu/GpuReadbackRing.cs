using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// A ring of fenced staging slots for reading frames back without ever waiting on the GPU. Each slot owns one
    /// <see cref="GpuTextureUsage.Staging"/> texture of <see cref="Width"/> x <see cref="Height"/>
    /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> and one fence. <see cref="TryRecordCopy"/> claims the next free
    /// slot inside the caller's open recording and hands back its fence for the submit that carries it.
    /// <see cref="TryReadCompleted"/> polls the oldest claimed slot's fence and reads it only once it has signaled.
    /// <para>
    /// <b>NOTHING HERE WAITS.</b> A completed slot is mapped through <see cref="IGpuFencedReadback.MapForRead"/>,
    /// never through <see cref="IGpuDevice.Map(IGpuTexture, GpuMapMode)"/>, whose read mode drains the whole device
    /// on native Metal and Vulkan. A tick with every slot in flight records nothing and returns false, so the
    /// caller drops that frame rather than stalling it.
    /// </para>
    /// <para>
    /// <b>FIFO BOTH WAYS.</b> Slots are claimed and read in the same order, so a later slot is never read ahead of
    /// an earlier one, even when its fence reads signaled first.
    /// </para>
    /// <para>
    /// <b>DISPOSE DOES NOT DRAIN.</b> Every staging texture and fence is freed at once. A staging texture freed
    /// while its copy is in flight is the backends' existing deferred-destruction case. Retire the ring once
    /// <see cref="PendingCount"/> reaches zero, or after the device drains at teardown.
    /// </para>
    /// </summary>
    public sealed class GpuReadbackRing : IDisposable
    {
        readonly IGpuDevice _device;
        readonly IGpuFencedReadback _readback;
        readonly IGpuTexture[] _staging;
        readonly IGpuFence[] _fences;
        readonly long[] _tags;
        int _oldest;
        int _pending;
        bool _disposed;

        /// <summary>Create <paramref name="slotCount"/> slots of <paramref name="width"/> x
        /// <paramref name="height"/>.</summary>
        /// <exception cref="NotSupportedException">The device reports no
        /// <see cref="GpuCapabilities.SupportsCompletionFences"/> or does not implement
        /// <see cref="IGpuFencedReadback"/>.</exception>
        public GpuReadbackRing(IGpuDevice device, int width, int height, int slotCount = 3)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
            if (!device.Capabilities.SupportsCompletionFences)
            {
                throw new NotSupportedException(
                    "GpuReadbackRing needs GpuCapabilities.SupportsCompletionFences, and "
                    + device.Capabilities.DeviceName + " reports none. Without a completion fence a slot can only "
                    + "be read after a device drain, which is the stall the ring exists to avoid.");
            }
            if (device is not IGpuFencedReadback readback)
            {
                throw new NotSupportedException(
                    "GpuReadbackRing needs a device that implements IGpuFencedReadback, and " + device.GetType().Name
                    + " does not. IGpuDevice.Map in read mode drains the whole device on native Metal and Vulkan, "
                    + "so the ring reads only through the fenced map.");
            }

            _device = device;
            _readback = readback;
            Width = width;
            Height = height;
            SlotCount = slotCount;
            _staging = new IGpuTexture[slotCount];
            _fences = new IGpuFence[slotCount];
            _tags = new long[slotCount];

            try
            {
                var description = GpuTextureDescription.Texture2D(
                    (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging);
                for (int i = 0; i < slotCount; i++)
                {
                    _staging[i] = device.Factory.CreateTexture(description);
                    _fences[i] = device.Factory.CreateFence();
                }
            }
            catch
            {
                DisposeSlots();
                throw;
            }
        }

        /// <summary>Slot width in pixels.</summary>
        public int Width { get; }

        /// <summary>Slot height in pixels.</summary>
        public int Height { get; }

        /// <summary>How many slots the ring holds.</summary>
        public int SlotCount { get; }

        /// <summary>Slots recorded and not yet read.</summary>
        public int PendingCount => _pending;

        /// <summary>Whether <see cref="TryRecordCopy"/> would claim a slot now.</summary>
        public bool HasFreeSlot => _pending < SlotCount;

        /// <summary>
        /// Record, into an OPEN recording, a whole copy of <paramref name="source"/> into the next free slot. Hand
        /// <paramref name="fence"/> to the <see cref="IGpuDevice.Submit(IGpuCommandList, IGpuFence)"/> that carries
        /// this recording. <paramref name="tag"/> comes back with the slot's bytes from
        /// <see cref="TryReadCompleted"/>.
        /// </summary>
        /// <returns>False, with nothing recorded and <paramref name="fence"/> null, when every slot is in
        /// flight.</returns>
        /// <exception cref="ArgumentException"><paramref name="source"/> is not a single-mip, single-sample
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> texture of exactly <see cref="Width"/> x
        /// <see cref="Height"/>.</exception>
        public bool TryRecordCopy(IGpuCommandList commands, IGpuTexture source, long tag,
            [MaybeNullWhen(false)] out IGpuFence fence)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(commands);
            RequireCopyableSource(source);

            fence = null;
            if (!HasFreeSlot) return false;

            int slot = (_oldest + _pending) % SlotCount;
            commands.CopyTexture(source, _staging[slot]);
            _tags[slot] = tag;
            _pending++;
            fence = _fences[slot];
            return true;
        }

        /// <summary>
        /// Read the oldest slot if its fence has signaled: map it through
        /// <see cref="IGpuFencedReadback.MapForRead"/>, de-stride it into <paramref name="destination"/> as packed
        /// RGBA rows, top row first, unmap it, reset its fence and free it. Never waits. Call only while nothing is
        /// recording on the device.
        /// </summary>
        /// <returns>False, with <paramref name="destination"/> untouched, when no slot is pending or the oldest
        /// one has not completed.</returns>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than
        /// <c>Width * Height * 4</c> bytes.</exception>
        public bool TryReadCompleted(Span<byte> destination, out long tag)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int rowBytes = Width * 4;
            if (destination.Length < rowBytes * Height)
            {
                throw new ArgumentException(
                    "A GpuReadbackRing read needs at least " + (rowBytes * Height).ToString(CultureInfo.InvariantCulture)
                    + " bytes (" + Width.ToString(CultureInfo.InvariantCulture) + "x"
                    + Height.ToString(CultureInfo.InvariantCulture) + " RGBA8) and was handed "
                    + destination.Length.ToString(CultureInfo.InvariantCulture) + ".",
                    nameof(destination));
            }

            tag = 0;
            if (_pending == 0) return false;
            IGpuFence fence = _fences[_oldest];
            if (!fence.Signaled) return false;

            IGpuTexture staging = _staging[_oldest];
            MappedData map = _readback.MapForRead(staging, fence);
            try
            {
                unsafe
                {
                    byte* data = (byte*)map.Data;
                    for (int y = 0; y < Height; y++)
                    {
                        new ReadOnlySpan<byte>(data + (long)y * map.RowPitch, rowBytes)
                            .CopyTo(destination.Slice(y * rowBytes, rowBytes));
                    }
                }
            }
            finally
            {
                _device.Unmap(staging);
            }

            fence.Reset();
            tag = _tags[_oldest];
            _oldest = (_oldest + 1) % SlotCount;
            _pending--;
            return true;
        }

        /// <summary>Free every slot's staging texture and fence without draining, in flight or not.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pending = 0;
            DisposeSlots();
        }

        void DisposeSlots()
        {
            for (int i = 0; i < _staging.Length; i++)
            {
                _staging[i]?.Dispose();
                _fences[i]?.Dispose();
            }
        }

        // The rule GpuReadback.RequireCopyableSource enforces, for the same reason: a whole copy names every
        // subresource on both sides, and Direct3D 11's CopyResource does not refuse a mismatch the way native
        // Metal and Vulkan do, so it is checked here, before anything is recorded.
        void RequireCopyableSource(IGpuTexture source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Format == GpuPixelFormat.R8G8B8A8UNorm && source.MipLevels == 1 && source.SampleCount == 1
                && source.Width == (uint)Width && source.Height == (uint)Height)
            {
                return;
            }

            string size = Width.ToString(CultureInfo.InvariantCulture) + "x"
                + Height.ToString(CultureInfo.InvariantCulture);
            throw new ArgumentException(
                "A readback ring copy (GpuReadbackRing.TryRecordCopy) into " + size + " slots was handed a texture "
                + "that is " + source.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + source.Height.ToString(CultureInfo.InvariantCulture) + " " + source.Format + " with "
                + source.MipLevels.ToString(CultureInfo.InvariantCulture) + " mip level(s) and "
                + source.SampleCount.ToString(CultureInfo.InvariantCulture) + " sample(s). Each slot is a " + size
                + " R8G8B8A8UNorm staging texture with 1 mip level and 1 sample, and a whole-texture copy names "
                + "every subresource on both sides, so the source has to be exactly that. Native Metal and Vulkan "
                + "refuse the copy themselves, Direct3D 11's CopyResource does not. Downscale a swapchain copy "
                + "into an R8G8B8A8UNorm target of the ring's size (GpuScaledCopy) before recording it.",
                nameof(source));
        }
    }
}
