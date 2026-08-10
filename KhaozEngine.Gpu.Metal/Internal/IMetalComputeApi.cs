using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE COMPUTE-ENCODER-SCOPED STATE SETTER, behind an interface so the schedule above it is device-free:
    /// <c>-setComputePipelineState:</c>, and nothing else. Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>ONE INTERFACE PER ENCODER KIND FOR THE UNCOUNTED EMISSIONS, WHICH IS WHY THIS EXISTS RATHER THAN
    /// A MEMBER ON A NEIGHBOUR.</b> <see cref="IMetalRenderApi"/> carries what a RENDER encoder is sent outside
    /// the counted classes, <see cref="IMetalBlitApi"/> what a BLIT encoder is sent, and this is the third kind.
    /// Folding this one member into the render seam would put a compute call behind a type whose every other
    /// member names a render encoder, and the two protocols are genuinely different: sending
    /// <c>-setRenderPipelineState:</c> to a compute encoder is an unrecognised selector, which is the same
    /// separation <see cref="MTLComputeCommandEncoder"/> exists for one level down.</para>
    ///
    /// <para><b>IT IS NOT <see cref="IMetalEncoderSink"/> AND MUST NOT BECOME IT</b>, which is the sentence both
    /// neighbours carry and the same reason. That seam covers the three call classes that scale with DRAW COUNT
    /// and M-T2 freezes a budget over it. A compute pipeline state is emitted once per pipeline change per
    /// encoder (M-R8 and M-R4 between them), so nothing about it scales with dispatch count, and widening the
    /// counted seam to reach it would quietly change what the budget means.</para>
    ///
    /// <para><b>THE DISPATCH ITSELF IS ON THE COUNTED SEAM</b>, because a dispatch IS one of M-T2's three
    /// classes. So a compute recording emits through both lines, exactly as a graphics one emits its state block
    /// through <see cref="IMetalRenderApi"/> and its draw through <see cref="IMetalEncoderSink"/>, and the split
    /// is the same one in both places: the command is counted, the state around it is observed.</para>
    ///
    /// <para><b>HANDLES ARE <see cref="IntPtr"/> AND NOTHING HERE NAMES AN OBJECTIVE-C TYPE</b>, so a fake
    /// invents plain numbers and the dispatch schedule runs on the Linux and Windows legs.</para>
    /// </summary>
    internal interface IMetalComputeApi
    {
        /// <summary>
        /// <c>-[MTLComputeCommandEncoder setComputePipelineState:]</c>, emitted by the pre-dispatch flush when
        /// <c>MetalPipelineBinding.NeedsComputeStateBlock</c> says the bound pipeline has not reached the encoder
        /// that is open now.
        /// </summary>
        /// <param name="encoder">The open compute encoder.</param>
        /// <param name="state">The <c>MTLComputePipelineState</c>. Never <see cref="IntPtr.Zero"/> from the
        /// shipped path: a disposed pipeline is refused at <c>SetComputePipeline</c>, and a pipeline that failed
        /// to create threw instead of existing.</param>
        void SetComputePipelineState(IntPtr encoder, IntPtr state);
    }

    /// <summary>
    /// The real one: a single message send, in the shape <see cref="MetalEncoderSink"/> established. A readonly
    /// struct with no state, so one instance serves every command list a device makes.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal readonly struct MetalComputeApi : IMetalComputeApi
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetComputePipelineState(IntPtr encoder, IntPtr state)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLComputeCommandEncoder(encoder).SetComputePipelineState(new MTLComputePipelineState(state));
        }
    }
}
