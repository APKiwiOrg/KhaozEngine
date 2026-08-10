using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A setup batch's native half with no device behind it, so everything <see cref="MetalSetupCommands"/>
    /// DECIDES (which uploads share a batch, when the byte budget commits one early, what a dead device releases
    /// and what it must not, and the disposal race) is driven by a plain <c>[Fact]</c> on a machine with no Metal
    /// at all.
    /// <para>
    /// HANDLES ARE COUNTERS, and that is the whole trick. <see cref="MTLCommandBuffer"/> and
    /// <see cref="MTLBuffer"/> are handle records over an <see cref="IntPtr"/>, so a fake can mint distinguishable
    /// ones without a driver, and the release bookkeeping below is then an exact statement about ownership rather
    /// than an approximation of it.
    /// </para>
    /// <para>
    /// WHAT NO FAKE HERE CAN PROVE is that Metal accepts the copy or that the batch completes.
    /// <c>MetalResourceGpuTests</c> is what proves that, under a <c>[GpuFact]</c> against a real device.
    /// </para>
    /// </summary>
    internal sealed class FakeMetalSetupNative : IMetalSetupNative
    {
        int _nextHandle = 1;

        /// <summary>Every batch opened, in order.</summary>
        internal List<MTLCommandBuffer> Batches { get; } = new();

        /// <summary>Every batch committed, in order. The count M-M9's claim is about.</summary>
        internal List<MTLCommandBuffer> Committed { get; } = new();

        /// <summary>Every batch released, in order. A batch released twice appears twice, which is how an
        /// over-release is caught here instead of as a crash somewhere else.</summary>
        internal List<MTLCommandBuffer> ReleasedBatches { get; } = new();

        /// <summary>Every staging buffer allocated, in order, with the byte count it was asked for.</summary>
        internal List<(MTLBuffer Buffer, int Length)> Staged { get; } = new();

        /// <summary>Every staging buffer released, in order.</summary>
        internal List<MTLBuffer> ReleasedStaging { get; } = new();

        /// <summary>Every encode, with the arguments it was given.</summary>
        internal List<(MTLCommandBuffer Batch, MTLBuffer Staged, MTLTexture Destination, ulong SourceRowPitch,
            MetalTextureUpload Upload)> Encoded { get; } = new();

        /// <summary>How many times a fault has been read.</summary>
        internal int FaultReads { get; private set; }

        /// <summary>What <see cref="ReadFault"/> answers. Completed unless a test wants a failure.</summary>
        internal MetalCommandBufferFault Fault { get; set; } = MetalCommandBufferFault.Completed;

        /// <summary>True to model a queue that will not make a command buffer, which is a device already in
        /// trouble and the one case an append silently does nothing.</summary>
        internal bool BeginAnswersNil { get; set; }

        /// <summary>The staging buffers allocated and not yet released: the residency the byte budget
        /// bounds.</summary>
        internal IReadOnlyList<(MTLBuffer Buffer, int Length)> LiveStaging
            => Staged.Where(s => !ReleasedStaging.Contains(s.Buffer)).ToArray();

        /// <summary>Those buffers' total bytes.</summary>
        internal long LiveStagingBytes => LiveStaging.Sum(s => (long)s.Length);

        /// <inheritdoc/>
        public MTLCommandBuffer BeginBatch()
        {
            if (BeginAnswersNil) return default;

            var batch = new MTLCommandBuffer(NextHandle());
            Batches.Add(batch);
            return batch;
        }

        /// <inheritdoc/>
        public MTLBuffer Stage(ReadOnlySpan<byte> data)
        {
            var staged = new MTLBuffer(NextHandle());
            Staged.Add((staged, data.Length));
            return staged;
        }

        /// <inheritdoc/>
        public void Encode(MTLCommandBuffer batch, MTLBuffer staged, MTLTexture destination, ulong sourceRowPitch,
            in MetalTextureUpload upload)
            => Encoded.Add((batch, staged, destination, sourceRowPitch, upload));

        /// <inheritdoc/>
        public void Commit(MTLCommandBuffer batch) => Committed.Add(batch);

        /// <inheritdoc/>
        public void ReleaseBatch(MTLCommandBuffer batch) => ReleasedBatches.Add(batch);

        /// <inheritdoc/>
        public void ReleaseStaging(MTLBuffer staged) => ReleasedStaging.Add(staged);

        /// <inheritdoc/>
        public MetalCommandBufferFault ReadFault(MTLCommandBuffer batch)
        {
            FaultReads++;
            return Fault;
        }

        IntPtr NextHandle() => new(_nextHandle++);
    }
}
