using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S <see cref="IGpuPipeline"/> ON THE NATIVE METAL BACKEND: an <c>MTLRenderPipelineState</c>, an
    /// <c>MTLDepthStencilState</c> when the pipeline draws into a depth target, and the whole resolved plan
    /// behind them. Work-breakdown row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>TWO OBJECTS AND NOT ONE, WHICH IS WHERE METAL SPLITS THE SEAM'S PIPELINE IN HALF.</b> A render
    /// pipeline state carries the shader functions, the vertex layout, the attachment formats and the blend
    /// state. Everything else the seam calls pipeline state (cull mode, winding, fill mode, depth clip, the
    /// blend colour, the stencil reference) is ENCODER state set per call, and the depth-stencil state is a third
    /// thing: its own object, bound with its own setter, and only legal on a pass that has a depth attachment.
    /// That is why <see cref="MetalPipelineState"/> exists beside the handles rather than being folded into
    /// them.</para>
    ///
    /// <para><b>THE DEPTH-STENCIL STATE IS CREATED ONLY FOR A PIPELINE THAT DECLARES A DEPTH OUTPUT, which is the
    /// creation half of the depth-target guard.</b> The incumbent does the same, inside
    /// <c>if (outputs.DepthAttachment != null)</c>, so a colour-only pipeline's state handle is nil. The emission
    /// half stays with the pre-draw block, gated on the BOUND FRAMEBUFFER having a depth target, because
    /// <c>-setDepthStencilState:</c> on a pass with no depth attachment is a validation error under the debug
    /// layer M-T7 arms on every native-leg run.</para>
    ///
    /// <para><b>IT CARRIES THE LIVENESS TOKEN AS ITS OWNER, NOT A LIST, which is the surface convention applied
    /// rather than restated.</b> A pipeline is a DEVICE-owned object with native handles of its own, created
    /// through the factory and disposed by its creator, so it is a resource in exactly the sense
    /// <see cref="MetalResourceOwnership"/> means: the question its identity has to answer is "which device
    /// released this", and the answer has to survive being bound into any number of command lists. The owner
    /// token <c>MetalCommandList.Owner</c> uses is the other convention, for a different question (which
    /// device's submit lock orders this list's queue), and it belongs to objects a list is bound INTO rather
    /// than objects bound into a list.</para>
    ///
    /// <para><b>DISPOSAL RELEASES BOTH HANDLES AND NEVER ON A DEAD DEVICE (M-F6)</b>, and the nil depth state
    /// makes its own release a no-op, so the colour-only case needs no branch.</para>
    /// </summary>
    internal sealed class MetalGraphicsPipeline : IGpuPipeline, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;

        // THE TWO HANDLES, held in fields rather than in the properties, because the properties refuse once
        // disposed and ReleaseOnMacOs runs AFTER the flag flips. Disposal is the one reader that must still see
        // them.
        readonly MTLRenderPipelineState _renderState;
        readonly MTLDepthStencilState _depthStencilState;

        /// <param name="liveness">The creating device's token, which is its identity.</param>
        /// <param name="plan">The resolved and checked plan. Built device-free by
        /// <see cref="MetalGraphicsPipelinePlan.Build"/>.</param>
        /// <param name="renderState">The <c>MTLRenderPipelineState</c> at +1, or nil in a device-free test.</param>
        /// <param name="depthState">The <c>MTLDepthStencilState</c> at +1, or nil for a pipeline with no depth
        /// output.</param>
        internal MetalGraphicsPipeline(IMetalDeviceLiveness liveness, MetalGraphicsPipelinePlan plan,
            MTLRenderPipelineState renderState, MTLDepthStencilState depthState)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(plan);

            _liveness = liveness;
            Plan = plan;
            _renderState = renderState;
            _depthStencilState = depthState;
        }

        /// <inheritdoc/>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>Everything this pipeline decided, resolved once at creation.</summary>
        internal MetalGraphicsPipelinePlan Plan { get; }

        /// <summary>
        /// The compiled pipeline state, bound with <c>-setRenderPipelineState:</c>.
        /// <para>
        /// IT THROWS ONCE DISPOSED RATHER THAN ANSWERING, which is <c>MetalShaderSet.FunctionFor</c>'s precedent
        /// and for its reason: <see cref="Dispose"/> RELEASES this object, so the field still holds a pointer
        /// whose last reference is gone, and handing it back would set a released
        /// <c>MTLRenderPipelineState</c> on a live encoder. That is a use-after-free inside the driver rather
        /// than anything this backend could report, and it is the failure a disposed-pipeline bind would
        /// otherwise reach.
        /// </para>
        /// </summary>
        /// <exception cref="ObjectDisposedException">This pipeline is disposed.</exception>
        internal MTLRenderPipelineState RenderState
        {
            get
            {
                RequireLive(nameof(RenderState));
                return _renderState;
            }
        }

        /// <summary>
        /// The depth-stencil state, or NIL when this pipeline declares no depth output. Nil is a legal and common
        /// value here rather than a failure, and it is half of the depth-target guard. Disposed, it throws for
        /// <see cref="RenderState"/>'s reason.
        /// </summary>
        /// <exception cref="ObjectDisposedException">This pipeline is disposed.</exception>
        internal MTLDepthStencilState DepthStencilState
        {
            get
            {
                RequireLive(nameof(DepthStencilState));
                return _depthStencilState;
            }
        }

        /// <summary>The rasterizer, depth and topology values a pipeline change emits.</summary>
        internal MetalPipelineState State => Plan.State;

        /// <summary>
        /// The seam's own scissor gate, which row 12 needs at every <c>SetPipeline</c>. Metal has no
        /// scissor-test enable at all (its rectangle is always live and defaults to the whole attachment), so
        /// honouring the flag is this backend reproducing the SEAM's rasterizer state rather than the API's, and
        /// not reproducing it makes a scissor set before a pipeline with the test off apply here and not on
        /// Direct3D 11.
        /// </summary>
        internal bool ScissorTestEnabled => Plan.State.ScissorTestEnabled;

        /// <summary>The binding table row 13 binds through and compares by reference on a switch (M-R9).</summary>
        internal MetalShaderIndexTable Table => Plan.Table;

        /// <summary>How many resource-set slots this pipeline declares, which is what a bind flush sizes its
        /// per-slot records to.</summary>
        internal int ResourceSlotCount => Plan.Layouts.Length;

        /// <summary>How many vertex streams this pipeline declares. Row 14 binds one buffer per slot at
        /// <see cref="MetalVertexStream.BufferIndex"/>.</summary>
        internal int VertexStreamCount => Plan.Streams.Length;

        /// <summary>True once disposed.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>
        /// THE ACCEPT PATH's WHOLE CHECK: the right backend, the right device, and NOT DISPOSED. What
        /// <c>SetPipeline</c> takes a pipeline through, in the shape <c>MetalResourceLayout.Require</c> and
        /// <c>MetalResourceSet.Require</c> already settled on.
        /// <para>
        /// THE DISPOSAL ARM IS THE ONE THIS TYPE ADDS A REASON TO. A disposed layout or set releases nothing at
        /// all on this backend, so refusing one is about the caller's belief that the declaration is still
        /// theirs. A disposed PIPELINE has released two Objective-C objects, so accepting one records a dangling
        /// handle into the recording and hands the pre-draw flush a released
        /// <c>MTLRenderPipelineState</c> to set on a live encoder. Refusing at the bind names the mistake at the
        /// call that made it, where the alternative is undefined behaviour inside the driver several calls later.
        /// </para>
        /// </summary>
        /// <param name="pipeline">The seam pipeline the caller passed.</param>
        /// <param name="owner">The calling device's liveness token, which is its identity.</param>
        /// <param name="parameterName">The entry point's own parameter name, for the exception.</param>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="ObjectDisposedException">A disposed pipeline.</exception>
        internal static MetalGraphicsPipeline Require(IGpuPipeline? pipeline, IMetalDeviceLiveness owner,
            string parameterName)
        {
            ArgumentNullException.ThrowIfNull(pipeline, parameterName);

            MetalGraphicsPipeline typed = MetalResourceOwnership.Require<MetalGraphicsPipeline>(
                pipeline, owner, parameterName);

            if (typed.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(MetalGraphicsPipeline),
                    MetalGraphicsPipelinePlan.Label + " that is already disposed was bound. Its "
                    + "MTLRenderPipelineState and MTLDepthStencilState have been released, so recording it would "
                    + "leave the draw that flushes it setting a released object on an encoder.");
            }

            return typed;
        }

        /// <summary>
        /// Build a graphics pipeline on <paramref name="device"/>: resolve and check everything device-free
        /// first, then make the two native calls.
        /// </summary>
        /// <exception cref="ShaderValidationException">Metal rejected the descriptor, or one of the device-free
        /// shader checks refused.</exception>
        [SupportedOSPlatform("macos")]
        internal static MetalGraphicsPipeline Create(MTLDevice device, IMetalDeviceLiveness liveness,
            in GpuPipelineDescription description)
            => CreateOnMacOs(device, liveness, MetalGraphicsPipelinePlan.Build(liveness, description));

        /// <inheritdoc/>
        /// <remarks>Releases both state objects, once, and never on a dead device (M-F6).</remarks>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        // The native half, and the ONLY member here that touches Metal. Everything it consumes has already been
        // resolved and refused on: what is left is writing two descriptors and reading two objects back.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalGraphicsPipeline CreateOnMacOs(MTLDevice device, IMetalDeviceLiveness liveness,
            MetalGraphicsPipelinePlan plan)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // FIRST, so a disposed shader set is refused before anything native is allocated. FunctionFor throws
            // by name for that case, in a message row 9 wrote for this exact call site.
            MTLFunction vertex = plan.Shaders.FunctionFor(MetalShaderStage.Vertex);
            MTLFunction fragment = plan.Shaders.FunctionFor(MetalShaderStage.Fragment);

            MTLRenderPipelineDescriptor descriptor = MTLRenderPipelineDescriptor.New();
            if (descriptor.IsNull) throw NoClass("MTLRenderPipelineDescriptor");

            MTLVertexDescriptor vertexDescriptor = MTLVertexDescriptor.New();
            if (vertexDescriptor.IsNull)
            {
                descriptor.Release();
                throw NoClass("MTLVertexDescriptor");
            }

            MTLRenderPipelineState renderState;
            try
            {
                descriptor.SetVertexFunction(vertex);
                descriptor.SetFragmentFunction(fragment);

                WriteVertexDescriptor(vertexDescriptor, plan);
                descriptor.SetVertexDescriptor(vertexDescriptor);

                for (int i = 0; i < plan.ColourAttachments.Length; i++)
                {
                    MetalColourAttachmentState colour = plan.ColourAttachments[i];
                    descriptor.ColorAttachmentAt((nuint)i).Configure(
                        colour.Format, colour.BlendingEnabled, colour.WriteMask,
                        colour.AlphaOperation, colour.SourceAlpha, colour.DestinationAlpha,
                        colour.ColourOperation, colour.SourceColour, colour.DestinationColour);
                }

                if (plan.DepthFormat is { } depthFormat) descriptor.SetDepthAttachmentPixelFormat(depthFormat);
                if (plan.StencilFormat is { } stencilFormat)
                    descriptor.SetStencilAttachmentPixelFormat(stencilFormat);

                // The incumbent's own conditional: 1 is the default and writing it changes nothing, so the two
                // paths agree either way and the shape stays comparable.
                if (plan.SampleCount > 1) descriptor.SetSampleCount((nuint)plan.SampleCount);

                renderState = device.NewRenderPipelineState(descriptor, out NSError error);
                if (renderState.IsNull) throw Rejected(error);
            }
            finally
            {
                // The vertex descriptor is COPIED by the property, so releasing it here is right rather than
                // merely safe: the pipeline never reads this object again.
                vertexDescriptor.Release();
                descriptor.Release();
            }

            MTLDepthStencilState depthState = default;
            if (plan.DepthFormat is not null)
            {
                try
                {
                    depthState = CreateDepthState(device, plan.State);
                }
                catch
                {
                    renderState.Release();
                    throw;
                }
            }

            return new MetalGraphicsPipeline(liveness, plan, renderState, depthState);
        }

        // M-B2 AND THE VERTEX PLAN, WRITTEN OUT. The layout index and the attribute's bufferIndex are the SAME
        // number by construction, because both come from MetalVertexStreamIndex.ForSlot: the incumbent computes it
        // twice from NonVertexBufferCount and getting the two out of step is what binds a vertex buffer where a
        // uniform should be.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void WriteVertexDescriptor(MTLVertexDescriptor vertexDescriptor, MetalGraphicsPipelinePlan plan)
        {
            foreach (MetalVertexStream stream in plan.Streams)
            {
                vertexDescriptor.LayoutAt(stream.BufferIndex)
                    .Configure(stream.Stride, stream.StepFunction, stream.StepRate);
            }

            foreach (MetalVertexAttribute attribute in plan.Attributes)
            {
                vertexDescriptor.AttributeAt(attribute.AttributeIndex).Configure(
                    MetalFormats.ToVertexFormat(attribute.Format), attribute.OffsetBytes, attribute.BufferIndex);
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MTLDepthStencilState CreateDepthState(MTLDevice device, MetalPipelineState state)
        {
            MTLDepthStencilDescriptor descriptor = MTLDepthStencilDescriptor.New();
            if (descriptor.IsNull) throw NoClass("MTLDepthStencilDescriptor");

            try
            {
                descriptor.Configure(state.DepthComparison, state.DepthWriteEnabled);
                MTLDepthStencilState depthState = device.NewDepthStencilState(descriptor);

                if (depthState.IsNull)
                {
                    throw new InvalidOperationException(
                        "The native Metal device would not create an MTLDepthStencilState. That call validates "
                        + "nothing and takes no error out-parameter, so a nil is a device already in trouble "
                        + "rather than a description this pipeline got wrong.");
                }

                return depthState;
            }
            finally
            {
                descriptor.Release();
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // THE FIELDS, not the properties: IsDisposed is already true by the time this runs, so the properties
            // would refuse the one caller that is entitled to the handles.
            _depthStencilState.Release();
            _renderState.Release();
        }

        // The precedent is MetalResourceLayout.Require and MetalResourceSet.Require, and this is the same guard
        // with a stronger reason behind it. A disposed layout or set releases NOTHING on this backend, so their
        // checks are about the caller's belief. A disposed pipeline has released two Objective-C objects, so the
        // handles it still holds are dangling and using one is a driver-side use-after-free.
        void RequireLive(string member)
        {
            if (!IsDisposed) return;

            throw new ObjectDisposedException(
                nameof(MetalGraphicsPipeline),
                MetalGraphicsPipelinePlan.Label + "'s " + member + " was read after the pipeline was disposed. "
                + "Disposal released the "
                + "MTLRenderPipelineState and the MTLDepthStencilState, so what is left is a pointer to an object "
                + "the device has already let go of, and setting it on an encoder is a use-after-free inside the "
                + "driver rather than anything this backend could report.");
        }

        // Metal's own words, because this is the one compatibility check the API performs for this backend and
        // the error names WHICH incompatibility: an attribute the vertex function does not declare, an attachment
        // format the fragment function does not write, a sample count the target does not have. Paraphrasing it
        // would throw away the only diagnostic there is.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ShaderValidationException Rejected(NSError error)
            => new("The native Metal device rejected this graphics pipeline: "
                + (error.IsNull
                    ? "-newRenderPipelineStateWithDescriptor:error: answered nil and wrote no NSError, which "
                        + "means the failure is not a compatibility one at all."
                    : error.LocalizedDescription())
                + " A pipeline is validated against its compiled functions, so this names a disagreement between "
                + "the shader set, the vertex layouts and the output formats this pipeline was created with.");

        static InvalidOperationException NoClass(string className)
            => new($"The Objective-C runtime has no {className} class, which means the Metal framework did not "
                + "load. Nothing about this pipeline description caused it.");
    }
}
