using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE NATIVE METAL PASS SCHEDULE ASKED FOR, as plain numbers, so every rule in sections 7.1 to 7.3 is
    /// asserted on the Linux and Windows legs with no Metal at all.
    /// <para>
    /// A READONLY STRUCT WITH ITS STATE BEHIND A CLASS, which is the emitter rule both sibling backends enforce
    /// and which <see cref="FakeMetalEncoderSink"/> states the load-bearing version of. It is less critical here
    /// (this seam is never consumed through a struct constraint) and it is kept anyway, so the two fakes a
    /// recording holds have one shape.
    /// </para>
    /// <para>
    /// IT MODELS THE DESCRIPTOR'S RETAIN AND RELEASE PAIR, not just the plan. The real API takes an
    /// <c>objc_retain</c> on every descriptor it builds because the object is autoreleased and has to survive
    /// until the encoder is opened in a different managed call, so
    /// <see cref="FakeMetalRenderCalls.OutstandingDescriptors"/> is what turns "exactly one release per
    /// acquisition, at every exit" into a device-free assertion instead of something only a leak check finds.
    /// </para>
    /// </summary>
    internal readonly struct FakeMetalRenderApi : IMetalRenderApi
    {
        internal FakeMetalRenderApi(FakeMetalRenderCalls calls) => Calls = calls;

        internal FakeMetalRenderCalls Calls { get; }

        public IntPtr CreateRenderPassDescriptor(ReadOnlySpan<MetalColourAttachment> colour,
            in MetalDepthAttachment depth)
            => Calls.CreateDescriptor(colour, in depth);

        public IntPtr CreateResolveDescriptor(IntPtr source, IntPtr destination)
            => Calls.CreateResolveDescriptor(source, destination);

        public void ReleaseRenderPassDescriptor(IntPtr descriptor) => Calls.ReleaseDescriptor(descriptor);

        public void SetGraphicsState(IntPtr encoder, in MetalGraphicsStateBlock block)
            => Calls.GraphicsState(encoder, in block);

        public void SetViewport(IntPtr encoder, float x, float y, float width, float height,
            float minDepth, float maxDepth)
            => Calls.Viewport(encoder, new MetalViewportRect(x, y, width, height, minDepth, maxDepth));

        public void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height)
            => Calls.Scissor(encoder, new MetalScissorRect(x, y, width, height));
    }

    /// <summary>
    /// THE COMPUTE-ENCODER STATE SETTER AS A LOG, which is what makes the pre-dispatch emission a device-free
    /// assertion: whether a dispatch emits <c>-setComputePipelineState:</c> at all is M-R8's identity guard and
    /// M-R4's encoder invalidation between them, and both are decisions a golden cannot see.
    /// </summary>
    internal sealed class FakeMetalComputeApi : IMetalComputeApi
    {
        /// <summary>Every pipeline-state bind, with the encoder it went into so a test can tell two compute
        /// encoders apart.</summary>
        internal List<(IntPtr Encoder, IntPtr State)> States { get; } = new();

        /// <inheritdoc/>
        public void SetComputePipelineState(IntPtr encoder, IntPtr state) => States.Add((encoder, state));
    }

    /// <summary>One pass descriptor exactly as the schedule asked for it, so a test reads load actions, store
    /// actions and clear values rather than a native object it cannot inspect.</summary>
    /// <param name="Colour">The colour attachments in order, copied because the schedule reuses its array.</param>
    /// <param name="Depth">The depth attachment, whose texture is <see cref="IntPtr.Zero"/> when there is
    /// none.</param>
    internal sealed record RecordedRenderPass(MetalColourAttachment[] Colour, MetalDepthAttachment Depth);

    /// <summary>
    /// The mutable half, held by reference so every copy of the fake writes into one record.
    /// </summary>
    internal sealed class FakeMetalRenderCalls
    {
        readonly List<string> _log = new();
        readonly List<RecordedRenderPass> _passes = new();
        readonly List<(IntPtr Encoder, MetalViewportRect Rect)> _viewports = new();
        readonly List<(IntPtr Encoder, MetalScissorRect Rect)> _scissors = new();
        readonly List<(IntPtr Encoder, MetalGraphicsStateBlock Block)> _stateBlocks = new();
        readonly List<(IntPtr Source, IntPtr Destination)> _resolves = new();
        readonly HashSet<IntPtr> _liveDescriptors = new();

        int _nextDescriptor = 0x9000;
        int _created;
        int _released;

        /// <summary>Every descriptor build, release, viewport and scissor in the order it was emitted.</summary>
        internal IReadOnlyList<string> Log => _log;

        /// <summary>Every pass the schedule described, in order. The load and store actions and the clear values
        /// M-A2 and M-A4 are about are read straight off these.</summary>
        internal IReadOnlyList<RecordedRenderPass> Passes => _passes;

        /// <summary>Every viewport emission, with the encoder it went to.</summary>
        internal IReadOnlyList<(IntPtr Encoder, MetalViewportRect Rect)> Viewports => _viewports;

        /// <summary>Every scissor emission.</summary>
        internal IReadOnlyList<(IntPtr Encoder, MetalScissorRect Rect)> Scissors => _scissors;

        /// <summary>Every pipeline-state block a draw emitted, with the encoder it went into. The DEPTH PAIR's
        /// guard is read straight off <see cref="MetalGraphicsStateBlock.DepthPair"/>, which is the decision the
        /// debug layer would otherwise be the only witness to.</summary>
        internal IReadOnlyList<(IntPtr Encoder, MetalGraphicsStateBlock Block)> StateBlocks => _stateBlocks;

        /// <summary>Every standalone resolve pass the recording asked for, source then destination.</summary>
        internal IReadOnlyList<(IntPtr Source, IntPtr Destination)> Resolves => _resolves;

        /// <summary>What is still retained. MUST be 0 after every exit, including the one where the encoder came
        /// back nil.</summary>
        internal int OutstandingDescriptors => _created - _released;

        /// <summary>Releases of a handle that was not live, counted separately because an over-release nets back
        /// to zero against a leak and is a use-after-free somewhere else entirely.</summary>
        internal int UnbalancedDescriptorReleases { get; private set; }

        /// <summary>Set to make the next build answer nil, which is Metal refusing a descriptor.</summary>
        internal bool NextCreateFails { get; set; }

        internal IntPtr CreateDescriptor(ReadOnlySpan<MetalColourAttachment> colour,
            in MetalDepthAttachment depth)
        {
            _passes.Add(new RecordedRenderPass(colour.ToArray(), depth));

            if (NextCreateFails)
            {
                NextCreateFails = false;
                _log.Add("descriptor -> nil");
                return IntPtr.Zero;
            }

            IntPtr descriptor = new(_nextDescriptor++);
            _log.Add($"descriptor {descriptor} colour x{colour.Length} depth={depth.Present}");
            _created++;
            _liveDescriptors.Add(descriptor);
            return descriptor;
        }

        internal void ReleaseDescriptor(IntPtr descriptor)
        {
            // The real API returns before the native call on a nil handle, so a refused build owes no release.
            if (descriptor == IntPtr.Zero) return;

            _log.Add($"release descriptor {descriptor}");
            _released++;
            if (!_liveDescriptors.Remove(descriptor)) UnbalancedDescriptorReleases++;
        }

        internal IntPtr CreateResolveDescriptor(IntPtr source, IntPtr destination)
        {
            _resolves.Add((source, destination));

            if (NextCreateFails)
            {
                NextCreateFails = false;
                _log.Add("resolve descriptor -> nil");
                return IntPtr.Zero;
            }

            IntPtr descriptor = new(_nextDescriptor++);
            _log.Add($"resolve descriptor {descriptor} {source} -> {destination}");
            _created++;
            _liveDescriptors.Add(descriptor);
            return descriptor;
        }

        internal void GraphicsState(IntPtr encoder, in MetalGraphicsStateBlock block)
        {
            _log.Add($"state block on {encoder} depthPair={block.DepthPair}");
            _stateBlocks.Add((encoder, block));
        }

        internal void Viewport(IntPtr encoder, MetalViewportRect rect)
        {
            _log.Add($"viewport {rect.X},{rect.Y} {rect.Width}x{rect.Height} on {encoder}");
            _viewports.Add((encoder, rect));
        }

        internal void Scissor(IntPtr encoder, MetalScissorRect rect)
        {
            _log.Add($"scissor {rect.X},{rect.Y} {rect.Width}x{rect.Height} on {encoder}");
            _scissors.Add((encoder, rect));
        }

        /// <summary>The clear value the LAST described pass folded onto colour attachment
        /// <paramref name="index"/>, or null when that attachment loaded instead. What M-A2's two positions
        /// differ by.</summary>
        internal Color? ClearOn(int index)
        {
            RecordedRenderPass pass = _passes[^1];
            return pass.Colour[index].LoadAction == MetalLoadAction.Clear ? pass.Colour[index].ClearValue : null;
        }
    }
}
