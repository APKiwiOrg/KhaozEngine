using System;
using System.Numerics;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE PIPELINE-STATE BLOCK AS ONE VALUE: the five calls a pipeline change always emits into a render
    /// encoder, plus the DEPTH TRIO and the one condition that decides whether it is emitted at all (section 6.3,
    /// work-breakdown row 14, https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>IT IS A VALUE SO THE GUARD IS A DEVICE-FREE ASSERTION RATHER THAN A CODE PATH INSIDE A NATIVE
    /// CALL.</b> The incumbent's <c>PreDrawCommand</c> writes the eight setters inline with the depth trio behind
    /// an <c>if (_framebuffer.DepthTarget != null)</c>, so the only way to check that condition there is to run a
    /// pass on a device and read what the debug layer says. Here the condition produces
    /// <see cref="DepthTrio"/> and the emission is one <c>IMetalRenderApi</c> call, which is the same split
    /// <see cref="MetalRenderPassSchedule"/> makes for the load and store actions and for the same reason: a
    /// decision that can be wrong runs on every leg, and the message send runs where there is a device.</para>
    ///
    /// <para><b>THE GUARD IS THE FRAMEBUFFER'S AND ONLY THE FRAMEBUFFER'S, WHICH IS THE INCUMBENT'S OWN CONDITION
    /// AND NOT THE ONE A READER EXPECTS.</b> Two things could plausibly gate the trio: the bound framebuffer
    /// having a depth attachment, or the bound pipeline declaring a depth output. The incumbent asks only the
    /// first, so a COLOUR-ONLY pipeline drawing into a framebuffer that has depth is sent
    /// <c>-setDepthStencilState:</c> with the NIL state object row 11 creates for it, which Metal reads as its
    /// own default (always pass, no write). Reproducing that is not cosmetic: the 36 committed <c>metal</c>
    /// goldens were baked through it, and a backend that skipped the call for a nil state would leave whatever
    /// the previous pipeline set in force, which is a depth test that silently keeps applying.</para>
    ///
    /// <para><b>AND THE OTHER DIRECTION IS THE ONE THE DEBUG LAYER CATCHES.</b> Sending the trio to a pass with no
    /// depth attachment is a validation error under <c>MTL_DEBUG_LAYER</c>, which M-T7 arms on every native-leg
    /// run, so this is one of the places the leg reports immediately rather than late. That asymmetry is why the
    /// condition is carried as a field rather than left to the caller to remember.</para>
    ///
    /// <para><b>HANDLES ARE <see cref="IntPtr"/>, which is <see cref="IMetalRenderApi"/>'s rule applied to the
    /// value that crosses it.</b> Nothing above the interop layer names an Objective-C object, so a fake records
    /// plain numbers and the rows above run with no Metal at all.</para>
    /// </summary>
    /// <param name="RenderState">The <c>MTLRenderPipelineState</c>, for <c>-setRenderPipelineState:</c>.</param>
    /// <param name="CullMode">For <c>-setCullMode:</c>.</param>
    /// <param name="FrontFace">For <c>-setFrontFacingWinding:</c>.</param>
    /// <param name="FillMode">For <c>-setTriangleFillMode:</c>.</param>
    /// <param name="BlendColour">For <c>-setBlendColorRed:green:blue:alpha:</c>, which the two constant blend
    /// factors read.</param>
    /// <param name="DepthTrio">Whether the three depth calls are emitted, which is the BOUND FRAMEBUFFER having a
    /// depth attachment and nothing else.</param>
    /// <param name="DepthStencilState">The <c>MTLDepthStencilState</c>, or <see cref="IntPtr.Zero"/> for a
    /// pipeline that declares no depth output. Meaningful only under <paramref name="DepthTrio"/>.</param>
    /// <param name="DepthClipMode">For <c>-setDepthClipMode:</c>.</param>
    /// <param name="StencilReference">For <c>-setStencilReferenceValue:</c>. Always 0, because the seam carries
    /// no stencil state at all.</param>
    internal readonly record struct MetalGraphicsStateBlock(
        IntPtr RenderState, MTLCullMode CullMode, MTLWinding FrontFace, MTLTriangleFillMode FillMode,
        Vector4 BlendColour, bool DepthTrio, IntPtr DepthStencilState, MTLDepthClipMode DepthClipMode,
        uint StencilReference)
    {
        /// <summary>
        /// The block a draw emits for <paramref name="pipeline"/> into a pass whose framebuffer is described by
        /// <paramref name="framebufferHasDepth"/>.
        /// </summary>
        /// <param name="pipeline">The bound graphics pipeline. Its two handle properties refuse once disposed,
        /// which is where a bind of a disposed pipeline is caught if it somehow got past <c>SetPipeline</c>.</param>
        /// <param name="framebufferHasDepth">Whether the BOUND framebuffer declares a depth attachment. See the
        /// type remarks: this is the whole of the guard.</param>
        /// <exception cref="ObjectDisposedException"><paramref name="pipeline"/> is disposed.</exception>
        internal static MetalGraphicsStateBlock For(MetalGraphicsPipeline pipeline, bool framebufferHasDepth)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            MetalPipelineState state = pipeline.State;
            return new MetalGraphicsStateBlock(
                pipeline.RenderState.Handle,
                state.CullMode,
                state.FrontFace,
                state.FillMode,
                state.BlendColour,
                framebufferHasDepth,
                pipeline.DepthStencilState.Handle,
                state.DepthClipMode,
                state.StencilReference);
        }
    }
}
