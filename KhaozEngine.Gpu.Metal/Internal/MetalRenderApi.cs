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
    /// nothing per pass and nothing per list, so one instance serves every command list a device makes, and
    /// <c>MetalGpuDevice</c> holds exactly that one and hands it to each list it creates. A list takes it as
    /// <see cref="IMetalRenderApi"/>, so constructing one per list would box one per list to describe a value
    /// that cannot differ.</para>
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
        /// <remarks>
        /// ONE ATTACHMENT, LOAD AND RESOLVE, AND NOTHING ELSE. There is no clear colour to send, because a pass
        /// that loads never reads one, and there is no depth slot: the incumbent's <c>ResolveTextureCore</c>
        /// builds exactly this descriptor and this reproduces it field for field.
        /// <para>
        /// THE SOURCE'S CONTENTS ARE DESTROYED BY THIS, which the incumbent's own TODO says and which is
        /// reproduced rather than fixed (M-C4): the engine re-clears its MSAA sources at the start of the next
        /// frame's pass, discarding is the bandwidth-correct answer on this architecture, and it is what
        /// <c>scene3d_hdr_msaa</c> was baked under. The divergence from <c>ResolveSubresource</c> and
        /// <c>vkCmdResolveImage</c> is documented in the package README so a consumer that ever needs the source
        /// preserved finds a property rather than a surprise.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr CreateResolveDescriptor(IntPtr source, IntPtr destination)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            MTLRenderPassDescriptor descriptor = MTLRenderPassDescriptor.Create();
            if (descriptor.IsNull) return IntPtr.Zero;

            MTLRenderPassAttachmentDescriptor slot = descriptor.ColourAttachment(0);
            if (slot.IsNull)
            {
                descriptor.Release();
                return IntPtr.Zero;
            }

            slot.SetTexture(new MTLTexture(source));
            slot.SetLoadAction(MTLLoadAction.Load);
            slot.SetStoreAction(MTLStoreAction.MultisampleResolve);
            slot.SetResolveTexture(new MTLTexture(destination));

            return descriptor.Handle;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE ORDER IS THE INCUMBENT'S <c>PreDrawCommand</c> BLOCK, call for call: the pipeline state, the cull
        /// mode, the winding, the fill mode, the blend colour, and then the depth trio behind the framebuffer
        /// guard. Nothing about the order is load-bearing to Metal, and it is kept anyway so a reader diffing the
        /// two sources sees one shape.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetGraphicsState(IntPtr encoder, in MetalGraphicsStateBlock block)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            var target = new MTLRenderCommandEncoder(encoder);
            target.SetRenderPipelineState(new MTLRenderPipelineState(block.RenderState));
            target.SetCullMode(block.CullMode);
            target.SetFrontFacingWinding(block.FrontFace);
            target.SetTriangleFillMode(block.FillMode);
            target.SetBlendColour(block.BlendColour.X, block.BlendColour.Y, block.BlendColour.Z,
                block.BlendColour.W);

            // THE GUARD IS THE FRAMEBUFFER'S ALONE, which is the incumbent's own condition and NOT the pipeline
            // declaring a depth output: a colour-only pipeline drawing into a framebuffer that has depth is sent
            // a NIL depth-stencil state, which Metal reads as its default. MetalGraphicsStateBlock carries the
            // whole argument, including why sending the trio to a depth-less pass is a debug-layer failure.
            if (!block.DepthTrio) return;

            target.SetDepthStencilState(new MTLDepthStencilState(block.DepthStencilState));
            target.SetDepthClipMode(block.DepthClipMode);
            target.SetStencilReferenceValue(block.StencilReference);
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
        /// <remarks>
        /// THE POOL IS HERE EVEN THOUGH <c>setViewports:count:</c> CREATES NO OBJECT, and the uniformity is the
        /// point rather than an oversight. M-N5's rule is enforced by an IL walk with no allowlist
        /// (<c>MetalAutoreleaseArchitectureTests</c>), so an entry point that reaches the interop layer opens a
        /// pool, full stop. The alternative is a rule with exceptions, which means every reader of every new
        /// selector has to decide whether THAT one autoreleases, and the incumbent's four-wrapped-sites-out-of-N
        /// shape is exactly what that decision-per-call-site produces. A push and a pop is two C calls on a path
        /// that runs once per framebuffer change.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetViewport(IntPtr encoder, float x, float y, float width, float height,
            float minDepth, float maxDepth)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            new MTLRenderCommandEncoder(encoder).SetViewport(
                new MTLViewport(x, y, width, height, minDepth, maxDepth));
        }

        /// <inheritdoc/>
        /// <remarks>Pooled for <see cref="SetViewport"/>'s reason.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetScissorRect(IntPtr encoder, uint x, uint y, uint width, uint height)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            new MTLRenderCommandEncoder(encoder).SetScissorRect(new MTLScissorRect(x, y, width, height));
        }

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
        // and Metal's is 1), so a cast would be silently wrong.
        //
        // EVERY NAMED MEMBER HAS ITS OWN ARM AND THE DISCARD THROWS, which is this package's absorbing-default
        // rule and is as far as the language allows the check to go. A switch expression listing every member and
        // no discard does NOT compile: CS8524 is an error here (warnings are errors), because an enum can hold a
        // value no member names. So a future member cannot be made a build break, and the next best thing is that
        // it cannot be silently absorbed either. #596's MultisampleResolve is the named candidate, and under an
        // absorbing `_ => Store` it would have emitted Store and resolved nothing, which is a wrong store action
        // with no error anywhere: exactly the class M-A4 exists to close.
        static MTLLoadAction ToLoadAction(MetalLoadAction action) => action switch
        {
            MetalLoadAction.Load => MTLLoadAction.Load,
            MetalLoadAction.Clear => MTLLoadAction.Clear,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action,
                "this MetalLoadAction has no MTLLoadAction. Both members are listed, so this is a new one, and "
                + "absorbing it into Load would silently load an attachment the plan asked to do something else "
                + "with."),
        };

        static MTLStoreAction ToStoreAction(MetalStoreAction action) => action switch
        {
            MetalStoreAction.Store => MTLStoreAction.Store,
            MetalStoreAction.DontCare => MTLStoreAction.DontCare,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action,
                "this MetalStoreAction has no MTLStoreAction. Both members are listed, so this is a new one, and "
                + "absorbing it into Store would emit a store where the plan asked for a resolve or a discard, "
                + "which completes with no error and the wrong contents."),
        };

        // The engine's Color is already four floats in 0 to 1, which is MTLClearColor's own range, so this widens
        // and does not rescale.
        static MTLClearColor ToClearColor(Primitives.Color colour)
            => new(colour.R, colour.G, colour.B, colour.A);
    }
}
