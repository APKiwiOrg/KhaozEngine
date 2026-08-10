using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// EVERYTHING A GRAPHICS PIPELINE DECIDES, DECIDED WITH NO DEVICE ANYWHERE. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577). <see cref="MetalGraphicsPipeline"/> turns one of
    /// these into two Objective-C objects and holds it.
    ///
    /// <para><b>IT IS A SEPARATE TYPE SO THE DECISIONS RUN ON EVERY LEG.</b> Pipeline creation is where four
    /// distinct refusals live (a shader set or a layout from another device, a declared layout array that
    /// disagrees with the shader's reflection, a vertex stream colliding with a resource buffer, and fewer blend
    /// states than colour attachments), and every one of them is a fact about managed data. Folding them into the
    /// constructor that also calls <c>-newRenderPipelineStateWithDescriptor:error:</c> would make them macOS-only,
    /// which on this program means they are asserted on one leg out of five.</para>
    ///
    /// <para><b>THE ORDER OF THE CHECKS IS THE ORDER THEY BECOME ANSWERABLE, and it matters for the message a
    /// caller sees.</b> Ownership first, because a layout from another device makes every later question
    /// meaningless. Then the layout SHAPE against the reflection (2.2b, pin 4), because the binding table is keyed
    /// on <c>(set, binding, stage)</c> read out of the shader's own decorations and a differently shaped array
    /// resolves every element through a key that means something else. Then the vertex plan, and only then M-B2's
    /// collision check, which needs the stream COUNT the plan produced.</para>
    ///
    /// <para><b>NOTHING HERE READS A LAYOUT'S PER-KIND COUNTS, and there are none to read (2.2b).</b> The
    /// incumbent's <c>MTLPipeline</c> constructor sums <c>MTLResourceLayout.BufferCount</c> across every layout
    /// into a <c>NonVertexBufferCount</c> that its vertex descriptor and its command list then both index from.
    /// That quantity does not exist on this backend, and M-B2's top-pinned stream numbering is what removes the
    /// need for it.</para>
    /// </summary>
    internal sealed class MetalGraphicsPipelinePlan
    {
        MetalGraphicsPipelinePlan(MetalShaderSet shaders, MetalResourceLayout[] layouts,
            MetalVertexStream[] streams, MetalVertexAttribute[] attributes, MetalPipelineState state,
            MetalColourAttachmentState[] colourAttachments, MTLPixelFormat? depthFormat,
            MTLPixelFormat? stencilFormat, int sampleCount)
        {
            Shaders = shaders;
            Layouts = layouts;
            Streams = streams;
            Attributes = attributes;
            State = state;
            ColourAttachments = colourAttachments;
            DepthFormat = depthFormat;
            StencilFormat = stencilFormat;
            SampleCount = sampleCount;
        }

        /// <summary>The name every refusal from this row quotes. The seam gives a pipeline no name of its own, so
        /// there is nothing more specific to say, and the message carries the numbers instead.</summary>
        internal const string Label = "A native Metal graphics pipeline";

        /// <summary>The compiled shader set, which is where the functions and the binding table come from.</summary>
        internal MetalShaderSet Shaders { get; }

        /// <summary>The declared resource layouts, in set order. A set bound at slot k indexes this array, and
        /// <c>k</c> is the <c>DescriptorSet</c> decoration the binding table is keyed on.</summary>
        internal MetalResourceLayout[] Layouts { get; }

        /// <summary>The vertex streams, indexed by the seam's vertex buffer SLOT. Empty for a fullscreen
        /// pass.</summary>
        internal MetalVertexStream[] Streams { get; }

        /// <summary>The vertex attributes, in attribute-index order.</summary>
        internal MetalVertexAttribute[] Attributes { get; }

        /// <summary>The rasterizer, depth and topology values a pipeline change emits.</summary>
        internal MetalPipelineState State { get; }

        /// <summary>Each colour output's format and blend state, in attachment order.</summary>
        internal MetalColourAttachmentState[] ColourAttachments { get; }

        /// <summary>The depth attachment's format, or null when this pipeline draws into no depth target. Null is
        /// also what decides that no <c>MTLDepthStencilState</c> is created at all.</summary>
        internal MTLPixelFormat? DepthFormat { get; }

        /// <summary>The stencil attachment's format, or null. Non-null only for a combined depth-stencil
        /// format.</summary>
        internal MTLPixelFormat? StencilFormat { get; }

        /// <summary>The target framebuffer's MSAA sample count, which a pipeline must match.</summary>
        internal int SampleCount { get; }

        /// <summary>The binding table, which travels with the shader set and is already canonical (row 10). Row 13
        /// binds through it and compares it by REFERENCE on a pipeline switch (M-R9).</summary>
        internal MetalShaderIndexTable Table => Shaders.Table;

        /// <summary>
        /// Resolve and check one graphics pipeline description.
        /// </summary>
        /// <param name="liveness">The creating device's identity token.</param>
        /// <param name="description">The seam's description.</param>
        /// <exception cref="ArgumentException">No shader set, a shader set or a layout from another backend or
        /// another device, or fewer blend states than colour attachments.</exception>
        /// <exception cref="ObjectDisposedException">A disposed resource layout.</exception>
        /// <exception cref="ArgumentOutOfRangeException">More vertex streams than the buffer table has entries, or
        /// a seam enum member with no Metal value.</exception>
        /// <exception cref="ShaderValidationException">The declared layout array is a different shape from the
        /// shader's reflection, or a vertex-stage resource buffer landed in the top-pinned stream range.</exception>
        internal static MetalGraphicsPipelinePlan Build(IMetalDeviceLiveness liveness,
            in GpuPipelineDescription description)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            if (description.ShaderSet is null)
            {
                throw new ArgumentException(
                    Label + " was given no shader set. A pipeline is created FROM a vertex and a fragment "
                    + "function, and the binding table it resolves every resource through travels with the set "
                    + "that was compiled, so there is nothing to build one from.",
                    nameof(description));
            }

            MetalShaderSet shaders = MetalResourceOwnership.Require<MetalShaderSet>(
                description.ShaderSet, liveness, nameof(description));

            MetalResourceLayout[] layouts = RequireLayouts(description.ResourceLayouts, liveness);

            // PIN 4 OF SECTION 2.2b, AND THIS IS ITS ONLY CALL SITE. Row 9 wrote the check and pipeline creation
            // is the first moment the ENGINE-declared array and the reflection the table was built from exist
            // together. Without it a pipeline whose layouts disagree with its shader resolves every element
            // through a key that means something else, which is the wrong-pixel-no-error class the whole
            // mechanism exists to close, arriving through the one door the id join leaves open.
            var declared = new GpuResourceLayoutDescription[layouts.Length];
            for (int i = 0; i < layouts.Length; i++) declared[i] = layouts[i].Description;
            shaders.Table.RequireLayoutShape(declared, Label);

            MetalVertexStream[] streams = MetalVertexPlan.Build(
                description.VertexLayouts, out MetalVertexAttribute[] attributes);

            // M-B2'S NO-COLLISION ASSERTION, which needs the stream count and therefore cannot be taken before
            // the plan above. It reads the vertex stage's own entries out of the table rather than anything the
            // layouts declare visible.
            MetalVertexStreams.RequireNoCollision(streams.Length, shaders.Table, Label);

            return new MetalGraphicsPipelinePlan(
                shaders,
                layouts,
                streams,
                attributes,
                MetalPipelineSpecs.ResolveState(description),
                MetalPipelineSpecs.ResolveColourAttachments(description, Label),
                MetalPipelineSpecs.ResolveDepthFormat(description.Outputs),
                MetalPipelineSpecs.ResolveStencilFormat(description.Outputs),
                description.Outputs.SampleCount);
        }

        // Every declared layout, cast and identity-checked through the one helper row 10 wrote for it, so a
        // pipeline and a resource set refuse the same wrong layout with the same message. A null ARRAY is the
        // no-resources case and is legal; a null ELEMENT is not, and MetalResourceLayout.Require names it.
        static MetalResourceLayout[] RequireLayouts(IGpuResourceLayout[]? declared, IMetalDeviceLiveness liveness)
        {
            if (declared is null || declared.Length == 0) return [];

            var layouts = new MetalResourceLayout[declared.Length];
            for (int i = 0; i < declared.Length; i++)
                layouts[i] = MetalResourceLayout.Require(declared[i], liveness, Label);

            return layouts;
        }
    }
}
