using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE REAL <see cref="IMetalRenderApi"/>: the pass descriptor built from a plan, and the two
    /// render-encoder-scoped setters, over the interop layer.
    ///
    /// <para><b>IT DECIDES NOTHING, AND THAT IS THE WHOLE SPLIT.</b> Which attachment clears (M-A2), what each
    /// load action is, that every store action is <c>Store</c> rather than the descriptor's discarding default
    /// (M-A4), whether the depth format carries a stencil plane, whether the viewport is owed and whether the
    /// scissor is gated out by the pipeline's <c>ScissorTestEnabled</c>, are all already answered in the plan
    /// this receives. <see cref="MetalRenderPassSchedule"/> is where every one of them is made and where a
    /// device-free test reads it. What is left here is translation and native calls, which is the part no test
    /// without a Metal device can run.</para>
    ///
    /// <para><b>A READONLY STRUCT WITH NO STATE, the emitter rule both sibling backends enforce.</b> It carries
    /// nothing per pass and nothing per list, so one instance serves every command list a device makes.</para>
    ///
    /// <para><b>EVERY BODY OPENS AN AUTORELEASE POOL (M-N5).</b> <c>+renderPassDescriptor</c> and every
    /// attachment slot it vends are autoreleased objects, and a pass built inside a frame loop with no pool
    /// accumulates all of them until something else drains. The descriptor's OWN lifetime is not what the pool
    /// covers: it is retained explicitly, because it has to outlive this call and reach the encoder's begin.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal readonly struct MetalRenderApi : IMetalRenderApi
    {
        /// <inheritdoc/>
        /// <remarks>
        /// THE COLOUR LOOP INDEXES BY <c>i</c>, WHICH IS THE ENTIRE OF M-A2's FIX. The incumbent writes every
        /// clear into <c>colorAttachments[0]</c>, so <c>ModelFB</c>'s normal and linear-depth attachments are
        /// never cleared at all. The fold onto slot 0 still HAPPENS under
        /// <c>KE_METAL_CLEAR=attachment0</c>, and it happens in the schedule where the pending clear is recorded,
        /// so the two positions differ by one plan and not by two code paths through here.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr CreateRenderPassDescriptor(ReadOnlySpan<MetalColourAttachment> colour,
            in MetalDepthAttachment depth)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLRenderPassDescriptor descriptor = MTLRenderPassDescriptor.Create();
            if (descriptor.IsNull) return IntPtr.Zero;

            for (int i = 0; i < colour.Length; i++)
            {
                MTLRenderPassAttachmentDescriptor slot = descriptor.ColourAttachment((uint)i);
                if (slot.IsNull) continue;

                slot.SetTexture(new MTLTexture(colour[i].Texture));
                slot.SetLoadAction(ToLoadAction(colour[i].LoadAction));
                slot.SetStoreAction(ToStoreAction(colour[i].StoreAction));

                // SENT UNCONDITIONALLY RATHER THAN ONLY UNDER Clear, because the descriptor is fresh and its
                // clear colour would otherwise be Metal's own default (opaque black) on an attachment that
                // LOADS. That is invisible today and would become a black frame the moment a later row set a
                // load action this backend does not select. One HFA call on a path that runs a handful of times
                // per frame.
                slot.SetClearColor(ToClearColor(colour[i].ClearValue));
            }

            if (depth.Present) DescribeDepth(descriptor, depth);

            return descriptor.Handle;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReleaseRenderPassDescriptor(IntPtr descriptor)
        {
            if (descriptor == IntPtr.Zero) return;

            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            new MTLRenderPassDescriptor(descriptor).Release();
        }

        /// <inheritdoc/>
        /// <remarks>No pool: <c>setViewports:count:</c> creates no object and returns none, so a pool here would
        /// be a push and a pop around nothing.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetViewport(IntPtr encoder, float x, float y, float width, float height,
            float minDepth, float maxDepth)
            => new MTLRenderCommandEncoder(encoder).SetViewport(
                new MTLViewport(x, y, width, height, minDepth, maxDepth));

        /// <inheritdoc/>
        /// <remarks>No pool, for <see cref="SetViewport"/>'s reason.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height)
            => new MTLRenderCommandEncoder(encoder).SetScissorRect(new MTLScissorRect(x, y, width, height));

        // THE DEPTH AND STENCIL PLANES, which Metal splits across two attachment slots over ONE texture where
        // the seam has one ClearDepthStencil carrying one float. The stencil slot is named only when the format
        // carries the plane, which is the incumbent's IsStencilFormat guard: naming it over a depth-only texture
        // is a validation error under the debug layer M-T7 arms on every run.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void DescribeDepth(MTLRenderPassDescriptor descriptor, in MetalDepthAttachment depth)
        {
            MTLLoadAction load = ToLoadAction(depth.LoadAction);
            MTLStoreAction store = ToStoreAction(depth.StoreAction);
            var texture = new MTLTexture(depth.Texture);

            MTLRenderPassAttachmentDescriptor slot = descriptor.DepthAttachment();
            if (!slot.IsNull)
            {
                slot.SetTexture(texture);
                slot.SetLoadAction(load);
                slot.SetStoreAction(store);
                slot.SetClearDepth(depth.ClearDepth);
            }

            if (!depth.Stencil) return;

            MTLRenderPassAttachmentDescriptor stencil = descriptor.StencilAttachment();
            if (stencil.IsNull) return;

            stencil.SetTexture(texture);
            stencil.SetLoadAction(load);
            stencil.SetStoreAction(store);

            // ZERO, ALWAYS, because the seam's ClearDepthStencil carries no stencil value and the incumbent
            // clears the plane to zero alongside the depth. Leaving the plane out entirely would leave it holding
            // whatever the last pass wrote, which is the undefined-is-not-stable case M-A4 rules on for stores.
            stencil.SetClearStencil(0);
        }

        // The two enum maps, as switches rather than casts. The values happen to differ (this backend's Load is 0
        // and Metal's is 1), so a cast would be silently wrong, and a switch with no default arm is what makes a
        // future member a compile error rather than a wrong load action.
        static MTLLoadAction ToLoadAction(MetalLoadAction action) => action switch
        {
            MetalLoadAction.Clear => MTLLoadAction.Clear,
            _ => MTLLoadAction.Load,
        };

        static MTLStoreAction ToStoreAction(MetalStoreAction action) => action switch
        {
            MetalStoreAction.DontCare => MTLStoreAction.DontCare,
            _ => MTLStoreAction.Store,
        };

        // The engine's Color is already four floats in 0 to 1, which is MTLClearColor's own range, so this widens
        // and does not rescale.
        static MTLClearColor ToClearColor(Primitives.Color colour)
            => new(colour.R, colour.G, colour.B, colour.A);
    }
}
