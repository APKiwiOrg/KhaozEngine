using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE COLOUR ATTACHMENT OF A RENDER PIPELINE, resolved: its pixel format and its whole blend state, in the
    /// values <c>MTLRenderPipelineColorAttachmentDescriptor</c> takes.
    /// </summary>
    /// <param name="Format">The attachment's pixel format, which must match the framebuffer's texture.</param>
    /// <param name="BlendingEnabled">Whether this attachment blends.</param>
    /// <param name="WriteMask">Which channels are written. Always every one, because the seam has no mask.</param>
    /// <param name="AlphaOperation">The alpha blend equation.</param>
    /// <param name="SourceAlpha">The alpha source factor.</param>
    /// <param name="DestinationAlpha">The alpha destination factor.</param>
    /// <param name="ColourOperation">The colour blend equation.</param>
    /// <param name="SourceColour">The colour source factor.</param>
    /// <param name="DestinationColour">The colour destination factor.</param>
    internal readonly record struct MetalColourAttachmentState(
        MTLPixelFormat Format, bool BlendingEnabled, MTLColorWriteMask WriteMask,
        MTLBlendOperation AlphaOperation, MTLBlendFactor SourceAlpha, MTLBlendFactor DestinationAlpha,
        MTLBlendOperation ColourOperation, MTLBlendFactor SourceColour, MTLBlendFactor DestinationColour);

    /// <summary>
    /// THE PIPELINE-STATE BLOCK'S VALUES, resolved once at creation: everything a pipeline CHANGE emits into a
    /// render encoder, plus the two the depth-stencil state object is built from.
    ///
    /// <para><b>RESOLVED HERE RATHER THAN AT THE BIND, because every one of them is a pure map over an enum.</b>
    /// The incumbent does the same thing in the same place, and the alternative is a switch per bind on a path
    /// that runs per pipeline change per frame.</para>
    ///
    /// <para><b>THE EMISSION IS NOT THIS ROW'S, AND THE SPLIT IS DELIBERATE.</b> Section 6.3's pipeline-state
    /// block is emitted from the PRE-DRAW flush, which is where the render encoder and the bound framebuffer both
    /// exist, so <c>-setRenderPipelineState:</c> and its five to eight companions land with the draw row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) together with their caller. What lands here is every
    /// decision that block makes, in a form a device-free test can read: which values it emits, and whether the
    /// depth trio is among them.</para>
    /// </summary>
    /// <param name="CullMode">Which faces the rasterizer discards.</param>
    /// <param name="FrontFace">Which winding is front.</param>
    /// <param name="FillMode">Solid or wireframe.</param>
    /// <param name="DepthClipMode">Clip or clamp against the near and far planes. Derived from the DEPTH TEST and
    /// not from the seam's own <see cref="GpuRasterizerState.DepthClipEnabled"/>, which is the incumbent's
    /// derivation reproduced and which https://github.com/APKiwiOrg/KhaozEngine/issues/598 records as a seam
    /// question rather than a backend one.</param>
    /// <param name="PrimitiveType">The topology, which is a DRAW argument on this API.</param>
    /// <param name="BlendColour">The constant blend colour, emitted with <c>-setBlendColor:</c> and read by the
    /// two constant blend factors.</param>
    /// <param name="ScissorTestEnabled">The seam's own scissor gate, which row 12 needs because Metal has no
    /// scissor-test enable at all.</param>
    /// <param name="StencilReference">Always 0. The seam has no stencil state, so there is no engine value this
    /// could come from, and it is named rather than left implicit because the emission carries it.</param>
    /// <param name="DepthComparison">The depth comparison the depth-stencil state is built with.</param>
    /// <param name="DepthWriteEnabled">Whether passing fragments write depth.</param>
    internal readonly record struct MetalPipelineState(
        MTLCullMode CullMode, MTLWinding FrontFace, MTLTriangleFillMode FillMode, MTLDepthClipMode DepthClipMode,
        MTLPrimitiveType PrimitiveType, Vector4 BlendColour, bool ScissorTestEnabled, uint StencilReference,
        MTLCompareFunction DepthComparison, bool DepthWriteEnabled);

    /// <summary>
    /// THE DEVICE-FREE HALF OF GRAPHICS PIPELINE CREATION: the seam's blend, depth, rasterizer, topology and
    /// output state turned into the values Metal's descriptors take. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>EVERY ANSWER HERE IS THE INCUMBENT'S, AND THAT IS THE POINT RATHER THAN A CONSTRAINT.</b> The 36
    /// committed <c>metal</c> goldens were baked through <c>Veldrid.MTL.MTLPipeline</c>'s choices, so a
    /// disagreement in any one of these maps moves pixels in a whole family at once, silently, with the goldens
    /// as the only witness. The two places this backend departs from it are both structural rather than
    /// behavioural, and both are argued where they happen: vertex stream numbering (M-B2,
    /// <see cref="MetalVertexStreamIndex"/>) and the compute pipeline's descriptor
    /// (<see cref="MTLComputePipelineState"/>).</para>
    ///
    /// <para><b>A DEPTH-TEST-OFF PIPELINE IS <c>Always</c> WITH WRITES OFF, which is where the seam's three-field
    /// depth state loses one field.</b> Metal's depth-stencil descriptor has no test enable: the test is the
    /// comparison, so "off" is the comparison that always passes. The incumbent reaches the same values through
    /// Veldrid's own <c>DepthStencilStateDescription</c>, which resolves the flag before Metal sees it. Doing it
    /// here rather than at the descriptor keeps the seam's whole depth state resolved in one place a test can
    /// read.</para>
    ///
    /// <para><b>THE SEAM'S DEPTH TEST FLAG IS READ TWICE, and the second read is the odd one.</b> It decides the
    /// comparison above, and it decides
    /// <see cref="MetalPipelineState.DepthClipMode"/>, because that is what the incumbent does with it. The
    /// second is not a rule anyone would invent from the seam, and it is reproduced rather than corrected for the
    /// golden reason. https://github.com/APKiwiOrg/KhaozEngine/issues/598 carries the correction as a seam
    /// decision.</para>
    /// </summary>
    internal static class MetalPipelineSpecs
    {
        /// <summary>
        /// Resolve the rasterizer, depth and topology state one pipeline change emits.
        /// </summary>
        /// <param name="description">The seam's pipeline description.</param>
        /// <exception cref="ArgumentOutOfRangeException">A seam enum member with no Metal value.</exception>
        internal static MetalPipelineState ResolveState(in GpuPipelineDescription description)
        {
            GpuRasterizerState rasterizer = description.Rasterizer;
            GpuDepthStencilState depth = description.DepthStencil;

            return new MetalPipelineState(
                MetalFormats.ToCullMode(rasterizer.CullMode),
                MetalFormats.ToWinding(rasterizer.FrontFace),
                MetalFormats.ToFillMode(rasterizer.FillMode),
                depth.DepthTestEnabled ? MTLDepthClipMode.Clip : MTLDepthClipMode.Clamp,
                MetalFormats.ToPrimitiveType(description.Topology),
                description.BlendFactor,
                rasterizer.ScissorTestEnabled,
                StencilReference: 0,

                // The seam's DepthTestEnabled has no Metal field to land in: a depth test that never rejects IS
                // the test being off, and a disabled test that still WROTE depth would be a state the seam
                // cannot express either, which is why the write flag is ANDed with it rather than passed through.
                depth.DepthTestEnabled ? MetalFormats.ToCompareFunction(depth.Comparison)
                    : MTLCompareFunction.Always,
                depth.DepthTestEnabled && depth.DepthWriteEnabled);
        }

        /// <summary>
        /// Resolve each colour output's format and blend state, in attachment order.
        /// </summary>
        /// <param name="description">The seam's pipeline description.</param>
        /// <param name="label">A name for the pipeline, quoted in the refusal.</param>
        /// <exception cref="ArgumentException">Fewer blend states than colour attachments, which is a pipeline
        /// that cannot be described at all.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A seam enum member with no Metal value.</exception>
        internal static MetalColourAttachmentState[] ResolveColourAttachments(
            in GpuPipelineDescription description, string label)
        {
            GpuPixelFormat[] colour = description.Outputs.Colour ?? [];
            GpuBlendAttachment[] blends = description.BlendAttachments ?? [];

            if (blends.Length < colour.Length)
            {
                throw new ArgumentException(
                    $"{label}: a native Metal graphics pipeline declares "
                    + $"{colour.Length.ToString(CultureInfo.InvariantCulture)} colour attachments and "
                    + $"{blends.Length.ToString(CultureInfo.InvariantCulture)} blend states. Metal carries the "
                    + "blend state ON the colour attachment descriptor, so every attachment needs one and there "
                    + "is no shared state to fall back to. The multiple-render-target passes rely on that: the "
                    + "model pass blends one attachment while preserving the destination of another.",
                    nameof(description));
            }

            // A LONGER BLEND ARRAY IS IGNORED RATHER THAN REFUSED, which is what both siblings do with one. An
            // attachment the pipeline does not have has nothing to blend into, and the incumbent's own loop is
            // over the colour attachments for the same reason.
            var states = new MetalColourAttachmentState[colour.Length];
            for (int i = 0; i < colour.Length; i++)
            {
                GpuBlendAttachment blend = blends[i];
                states[i] = new MetalColourAttachmentState(
                    // depthFormat: false, because this is a colour attachment. R32Float is the one seam format
                    // that means two different Metal formats, and reading it as a depth format here would give
                    // the linear-depth MRT target a format the fragment function cannot write.
                    MetalFormats.ToPixelFormat(colour[i], depthFormat: false),
                    blend.BlendEnabled,
                    MTLColorWriteMask.All,
                    MetalFormats.ToBlendOperation(blend.AlphaFunction),
                    MetalFormats.ToBlendFactor(blend.SourceAlphaFactor),
                    MetalFormats.ToBlendFactor(blend.DestinationAlphaFactor),
                    MetalFormats.ToBlendOperation(blend.ColorFunction),
                    MetalFormats.ToBlendFactor(blend.SourceColorFactor),
                    MetalFormats.ToBlendFactor(blend.DestinationColorFactor));
            }

            return states;
        }

        /// <summary>
        /// The depth attachment's pixel format, or null when the pipeline draws into no depth target.
        /// <para>
        /// <c>depthFormat: true</c> IS LOAD-BEARING HERE. <see cref="GpuPixelFormat.R32Float"/> is a colour format
        /// as an MRT target and a depth format on a shadow map, and Metal has two different pixel formats for
        /// those. Getting it backwards gives the shadow pass a target it cannot write, which is a black shadow
        /// map rather than an error.
        /// </para>
        /// </summary>
        internal static MTLPixelFormat? ResolveDepthFormat(in GpuOutputDescription outputs)
            => outputs.Depth is { } depth ? MetalFormats.ToPixelFormat(depth, depthFormat: true) : null;

        /// <summary>
        /// The stencil attachment's pixel format, or null. Written only for a COMBINED depth-stencil format,
        /// which is the incumbent's own condition: naming a stencil format a depth-only texture does not carry
        /// makes the pipeline incompatible with its framebuffer.
        /// </summary>
        internal static MTLPixelFormat? ResolveStencilFormat(in GpuOutputDescription outputs)
            => outputs.Depth is { } depth && MetalFormats.IsStencilFormat(depth)
                ? MetalFormats.ToPixelFormat(depth, depthFormat: true)
                : null;
    }
}
