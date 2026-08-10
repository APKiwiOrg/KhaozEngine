using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A <see cref="IMetalStagingSource"/> WITH NO DEVICE BEHIND IT: every block is a pinned managed array and a
    /// handle that is just a number, so the arena's size classes, its sub-allocation, its recycling boundary and
    /// its retention cap are all driven by plain <c>[Fact]</c>s on a machine with no Metal at all.
    /// <para>
    /// THE BYTES ARE REAL, which is what lets a routing test assert WHERE a payload landed rather than only that
    /// a lease was taken. A block's mapped pointer addresses its pinned array, so a caller writing through the
    /// lease writes into <see cref="Contents"/> and the test reads it back.
    /// </para>
    /// </summary>
    internal sealed class FakeMetalStagingSource : IMetalStagingSource, IDisposable
    {
        readonly Dictionary<IntPtr, (byte[] Bytes, GCHandle Pin)> _blocks = new();
        nint _nextHandle = 0x5000;

        /// <summary>Every block ever created, in order, with the size it was asked for. The pool's whole
        /// observable behaviour: a run of uploads that reuses blocks leaves this short.</summary>
        internal List<ulong> Created { get; } = new();

        /// <summary>Every block released, in order.</summary>
        internal List<ulong> Destroyed { get; } = new();

        /// <summary>What one block currently holds.</summary>
        internal byte[] Contents(IntPtr handle) => _blocks[handle].Bytes;

        /// <inheritdoc/>
        public MetalStagingBlock Create(ulong sizeBytes)
        {
            Created.Add(sizeBytes);

            var bytes = new byte[(int)sizeBytes];
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr handle = _nextHandle;
            _nextHandle += 0x100;

            _blocks[handle] = (bytes, pin);
            return new MetalStagingBlock(handle, pin.AddrOfPinnedObject(), sizeBytes);
        }

        /// <inheritdoc/>
        public void Destroy(in MetalStagingBlock block)
        {
            Destroyed.Add(block.SizeBytes);

            if (!_blocks.Remove(block.Buffer, out (byte[] Bytes, GCHandle Pin) held)) return;
            if (held.Pin.IsAllocated) held.Pin.Free();
        }

        /// <summary>Free anything the arena did not, so a test that never disposes its arena does not leak a
        /// pinned array into the rest of the run.</summary>
        public void Dispose()
        {
            foreach ((byte[] _, GCHandle pin) in _blocks.Values)
            {
                if (pin.IsAllocated) pin.Free();
            }

            _blocks.Clear();
        }
    }

    /// <summary>
    /// EVERY BLIT-ENCODER TRANSFER AS A LOG RATHER THAN A MESSAGE SEND, which is what makes two claims testable
    /// off a device. Row 8's: a record-time write to a UNIFORM buffer emits NOTHING here and a write to any other
    /// buffer emits exactly one entry. And row 14's: which of the four staging cases a texture copy fans out to,
    /// and the byte offsets and pitches the staging side supplies, which is the arithmetic that garbles every
    /// golden readback at once when it is wrong. See <see cref="IMetalBlitApi"/>.
    /// </summary>
    internal sealed class FakeMetalBlitApi : IMetalBlitApi
    {
        /// <summary>Every buffer-to-buffer copy encoded, in order.</summary>
        internal List<(IntPtr Encoder, IntPtr Source, ulong SourceOffset, IntPtr Destination,
            ulong DestinationOffset, ulong Size)> Copies { get; } = new();

        /// <summary>Every texture-to-texture copy, in order.</summary>
        internal List<(IntPtr Encoder, IntPtr Source, IntPtr Destination, MetalTextureRegion Region)>
            TextureCopies { get; } = new();

        /// <summary>Every readback (texture into a staging buffer), in order.</summary>
        internal List<(IntPtr Encoder, IntPtr Source, IntPtr Destination, MetalBufferImageRegion Region)>
            Readbacks { get; } = new();

        /// <summary>Every upload (staging buffer into a texture), in order.</summary>
        internal List<(IntPtr Encoder, IntPtr Source, IntPtr Destination, MetalBufferImageRegion Region)>
            Uploads { get; } = new();

        /// <summary>Every mip-chain generation, in order.</summary>
        internal List<(IntPtr Encoder, IntPtr Texture)> MipChains { get; } = new();

        /// <inheritdoc/>
        public void CopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffsetBytes, IntPtr destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
            => Copies.Add((encoder, source, sourceOffsetBytes, destination, destinationOffsetBytes, sizeBytes));

        /// <inheritdoc/>
        public void CopyTextureToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalTextureRegion region)
            => TextureCopies.Add((encoder, source, destination, region));

        /// <inheritdoc/>
        public void CopyTextureToBuffer(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region)
            => Readbacks.Add((encoder, source, destination, region));

        /// <inheritdoc/>
        public void CopyBufferToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region)
            => Uploads.Add((encoder, source, destination, region));

        /// <inheritdoc/>
        public void GenerateMipmaps(IntPtr encoder, IntPtr texture) => MipChains.Add((encoder, texture));
    }
}
