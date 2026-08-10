using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE NATIVE METAL RECORDER ASKED THE DRIVER FOR, as plain numbers, so every test above the interop
    /// layer runs on Linux and Windows with no Metal at all. The counting half of decision M-T2's budget seam.
    /// <para>
    /// A READONLY STRUCT WITH ITS STATE BEHIND A CLASS, which is the emitter rule
    /// <see cref="IMetalEncoderSink"/> states and which is load-bearing here rather than stylistic: that seam is
    /// consumed BOXED on the boundary path (the encoder scope holds one as a field) and UNBOXED through a struct
    /// constraint on the per-draw path, so a sink with a mutable field would count boundaries into one copy and
    /// draws into another and every budget number would be wrong in the direction that looks fine.
    /// </para>
    /// </summary>
    internal readonly struct FakeMetalEncoderSink : IMetalEncoderSink
    {
        internal FakeMetalEncoderSink(FakeMetalEncoderCalls calls) => Calls = calls;

        internal FakeMetalEncoderCalls Calls { get; }

        public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor)
            => Calls.BeginEncoder(MetalEncoderKind.Render, commandBuffer, descriptor);

        public IntPtr BeginBlitEncoder(IntPtr commandBuffer)
            => Calls.BeginEncoder(MetalEncoderKind.Blit, commandBuffer, IntPtr.Zero);

        public IntPtr BeginComputeEncoder(IntPtr commandBuffer)
            => Calls.BeginEncoder(MetalEncoderKind.Compute, commandBuffer, IntPtr.Zero);

        public void EndEncoding(MetalEncoderKind kind, IntPtr encoder) => Calls.EndEncoder(kind, encoder);

        public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
            => Calls.ArgumentTableWrite($"buffers[{stage}] x{buffers.Length} @{firstIndex}");

        public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
            uint firstIndex)
            => Calls.ArgumentTableWrite($"textures[{stage}] x{textures.Length} @{firstIndex}");

        public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
            => Calls.ArgumentTableWrite($"samplers[{stage}] x{samplers.Length} @{firstIndex}");

        public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index)
            => Calls.ArgumentTableWrite($"bufferOffset[{stage}] {offset} @{index}");

        public void Draw(IntPtr encoder, uint vertexStart, uint vertexCount, uint instanceCount,
            uint baseInstance)
            => Calls.Draw($"draw {vertexCount}v x{instanceCount}");

        public void DrawIndexed(IntPtr encoder, uint indexCount, IntPtr indexBuffer, nuint indexBufferOffset,
            bool sixteenBitIndices, uint instanceCount, int baseVertex, uint baseInstance)
            => Calls.Draw($"drawIndexed {indexCount}i x{instanceCount}");

        public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
            uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ)
            => Calls.Draw($"dispatch {groupCountX}x{groupCountY}x{groupCountZ}");
    }

    /// <summary>
    /// The mutable half, held by reference so every copy of the sink writes into one record.
    /// <para>
    /// IT MODELS THE RETAIN AND RELEASE PAIR, not just the call counts. The real sink takes an explicit
    /// <c>objc_retain</c> on every encoder it opens and balances it inside <c>EndEncoding</c>, so an abandoned
    /// encoder leaks that +1 AND, through the reference an encoder holds on its command buffer, keeps a buffer
    /// counted against the queue's uncommitted maximum for the life of the process. That is a hang on the 65th
    /// command buffer rather than a wrong number, so <see cref="OutstandingEncoders"/> is what makes it a
    /// device-free assertion instead of something only a soak finds.
    /// </para>
    /// </summary>
    internal sealed class FakeMetalEncoderCalls
    {
        readonly List<string> _log = new();
        readonly List<IntPtr> _retainedEncoders = new();
        readonly List<IntPtr> _releasedEncoders = new();
        readonly HashSet<IntPtr> _live = new();

        int _nextEncoder = 0x1000;

        /// <summary>Every boundary, argument-table write and draw in the order it was emitted.</summary>
        internal IReadOnlyList<string> Log => _log;

        /// <summary>M-T2's third call class, counted: the begin and end of every encoder kind.</summary>
        internal int EncoderBoundaries { get; private set; }

        /// <summary>Just the begins, which is what "how many passes did this frame open" asks.</summary>
        internal int EncoderBegins { get; private set; }

        /// <summary>M-T2's first call class.</summary>
        internal int ArgumentTableWrites { get; private set; }

        /// <summary>M-T2's second call class.</summary>
        internal int DrawsAndDispatches { get; private set; }

        /// <summary>Set to make the next begin of <see cref="NilForKind"/> hand back nil, which is M-W5's
        /// orphan-target case for a render encoder and a device in trouble for the other two.</summary>
        internal MetalEncoderKind NilForKind { get; set; } = MetalEncoderKind.None;

        /// <summary>Every encoder the sink handed back and took a retain on, in order. A nil begin takes none,
        /// which is what the real sink's <c>Retained</c> helper does.</summary>
        internal IReadOnlyList<IntPtr> RetainedEncoders => _retainedEncoders;

        /// <summary>Every encoder that was ended, and therefore released, in order.</summary>
        internal IReadOnlyList<IntPtr> ReleasedEncoders => _releasedEncoders;

        /// <summary>What is still retained. MUST be 0 after every exit: an encoder left open holds its own +1 and
        /// its command buffer's, and the buffer stays counted against the queue's uncommitted maximum.</summary>
        internal int OutstandingEncoders => _retainedEncoders.Count - _releasedEncoders.Count;

        /// <summary>Ends of a handle that was not live. An over-release of an Objective-C object is a
        /// use-after-free somewhere else entirely, so it is counted rather than left to the balance, which a
        /// double end plus a leak would net back to zero.</summary>
        internal int UnbalancedEncoderReleases { get; private set; }

        internal IntPtr BeginEncoder(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor)
        {
            // THE FAKE IS STRICT ABOUT THE ONE ARGUMENT IT WOULD OTHERWISE HIDE. The real
            // renderCommandEncoderWithDescriptor: takes a nonnull descriptor, so a nil one is undefined on a
            // device, and a fake that ignores the argument hands back a perfectly good encoder for it and keeps
            // the whole device-free suite green through a defect that only a Mac can see. That is exactly how the
            // first draft of the pass schedule ran a refused descriptor into the encoder factory. The other two
            // kinds take no descriptor and pass Zero, so the check is keyed on the kind.
            if (kind == MetalEncoderKind.Render && descriptor == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "The fake encoder sink was asked for a RENDER encoder with a nil descriptor. The real "
                    + "selector takes a nonnull argument, so this would be undefined behaviour on a device and "
                    + "the caller owes the pass schedule's nil arm instead.", nameof(descriptor));
            }

            if (kind == NilForKind)
            {
                _log.Add($"begin {kind} -> nil");
                EncoderBoundaries++;
                EncoderBegins++;
                return IntPtr.Zero;
            }

            IntPtr encoder = new(_nextEncoder++);
            _log.Add($"begin {kind} on {commandBuffer} -> {encoder}");
            EncoderBoundaries++;
            EncoderBegins++;
            _retainedEncoders.Add(encoder);
            _live.Add(encoder);
            return encoder;
        }

        internal void EndEncoder(MetalEncoderKind kind, IntPtr encoder)
        {
            // The real sink returns before the native call for a nil handle, so no boundary is emitted and there
            // is no retain to balance.
            if (encoder == IntPtr.Zero) return;

            _log.Add($"end {kind} {encoder}");
            EncoderBoundaries++;

            _releasedEncoders.Add(encoder);
            if (!_live.Remove(encoder)) UnbalancedEncoderReleases++;
        }

        internal void ArgumentTableWrite(string what)
        {
            _log.Add(what);
            ArgumentTableWrites++;
        }

        internal void Draw(string what)
        {
            _log.Add(what);
            DrawsAndDispatches++;
        }
    }

    /// <summary>
    /// A command-buffer source that hands out opaque numbers and remembers what it lent and what came back, so
    /// the list's ownership rule (exactly one release per acquisition, at exactly one of the three exits) is a
    /// device-free assertion rather than a comment.
    /// </summary>
    internal sealed class FakeMetalCommandBufferSource : IMetalCommandBufferSource
    {
        readonly List<IntPtr> _acquired = new();
        readonly List<IntPtr> _released = new();

        int _next = 0x100;

        /// <summary>Set to make the next <see cref="Acquire"/> answer nil, which is the queue refusing.</summary>
        internal bool NextAcquireFails { get; set; }

        internal IReadOnlyList<IntPtr> Acquired => _acquired;

        internal IReadOnlyList<IntPtr> Released => _released;

        /// <summary>What the list still holds, which must be empty after a dispose.</summary>
        internal int Outstanding => _acquired.Count - _released.Count;

        public IntPtr Acquire()
        {
            if (NextAcquireFails)
            {
                NextAcquireFails = false;
                return IntPtr.Zero;
            }

            IntPtr buffer = new(_next++);
            _acquired.Add(buffer);
            return buffer;
        }

        public void Release(IntPtr commandBuffer)
        {
            if (commandBuffer != IntPtr.Zero) _released.Add(commandBuffer);
        }
    }
}
