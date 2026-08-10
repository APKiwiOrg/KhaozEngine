using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// AN <c>MTLRenderCommandEncoder</c>, with the two members the PASS row emits: the viewport and the scissor
    /// rectangle.
    ///
    /// <para><b>THE REST OF THIS PROTOCOL LANDS WITH THE ROWS THAT CALL IT</b>, which is the rule
    /// <c>MetalEncoderSink</c> states and the reason this file stays short: a native prototype added by a row with
    /// no caller and no test that runs it is an Objective-C declaration nobody has ever executed, and a wrong ABI
    /// assumption in interop is a memory corruption rather than a compile error. The argument-table setters are
    /// here now, with the bind flush (https://github.com/APKiwiOrg/KhaozEngine/issues/579) calling them. The
    /// draws arrive with row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580) and the pipeline-state
    /// block with row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577).</para>
    ///
    /// <para><b>EIGHT SELECTORS LIVE HERE AND ALL EIGHT HAVE BEEN SENT TO A REAL ENCODER</b>, which is a stronger
    /// statement than it looks and was not true when they landed. The four members below are four PAIRS, and
    /// <c>MetalBindFlushGpuTests</c>'s original fixture had a vertex function reading one buffer, so the vertex
    /// halves of the texture and sampler setters were never executed by anything. That is exactly the class of
    /// gap the rule above exists to close, since an unrecognised selector aborts the process rather than
    /// producing a wrong pixel, so a second fixture whose vertex stage samples now drives them and the test reads
    /// the executed set off the sink's own log rather than inferring it from a run that did not throw.</para>
    ///
    /// <para><b>THE FOUR ARGUMENT-TABLE SETTERS TAKE THE STAGE AS AN ARGUMENT, because on this protocol the stage
    /// is spelled INSIDE the selector.</b> <c>setVertexBuffers:offsets:withRange:</c> and
    /// <c>setFragmentBuffers:offsets:withRange:</c> are two selectors with one signature, so a member per stage
    /// would be eight near-identical bodies over four prototypes. Forking on
    /// <see cref="MetalShaderStage"/> inside each keeps the selector pair beside the prototype it is sent
    /// through, which is what this folder's one-file-per-class rule is for. The compute siblings live on
    /// <see cref="MTLComputeCommandEncoder"/> because they belong to a different protocol, and
    /// <see cref="MetalShaderStage.Compute"/> is refused here by name rather than silently binding to the vertex
    /// stage.</para>
    ///
    /// <para><b><c>-endEncoding</c> IS NOT HERE, deliberately.</b> It belongs to the protocol all three kinds
    /// share and lives once on <see cref="MTLCommandEncoder"/>, which is what
    /// <see cref="MetalEncoderScope"/> drives every transition through.</para>
    ///
    /// <para><b>BOTH SETTERS ARE THE PLURAL FORM, UNCONDITIONALLY, AT A COUNT OF 1 (M-A7).</b> The incumbent's
    /// <c>FlushViewports</c> picks between <c>setViewports:count:</c> and the singular setter on
    /// <c>IsSupported(macOS_GPUFamily1_v3)</c>, which is a deprecated-feature-set read on the hot path to choose
    /// between two calls that do the same thing at count 1. The seam has no multi-viewport concept, so the count
    /// is always 1 and one code path is the answer. Taking the plural also removes an ABI question rather than
    /// adding one: the singular forms pass their structs BY VALUE, which is the indirect-composite path row 1's
    /// spike had to measure, where these pass an array ADDRESS and a count, two plain register arguments.</para>
    ///
    /// <para><b>NEITHER IS AN <see cref="IMetalEncoderSink"/> CALL, and that is M-T2's line rather than an
    /// oversight.</b> A viewport and a scissor are emitted once per framebuffer change and once per encoder
    /// boundary, so nothing about them scales with draw count, and freezing a budget marginal over them would
    /// gate on a figure nobody should gate on. They are emitted through <see cref="IMetalRenderApi"/> instead,
    /// which exists so section 7.3's three assertions can run with no Metal at all.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLRenderCommandEncoder(IntPtr Handle)
    {
        /// <summary>True when the command buffer would not make one, which is M-W5's orphan-target case on a
        /// framebuffer whose drawable came back nil.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-setViewports:count:</c> with one viewport.
        /// <para>
        /// THE ADDRESS IS TAKEN OF A LOCAL AND THE CALL DOES NOT OUTLIVE IT. Metal copies the array's contents
        /// during the call, which is what makes a stack address legal here: an encoder holds the viewport as its
        /// own state afterwards and never reads the caller's memory again.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetViewport(in MTLViewport viewport)
        {
            MTLViewport value = viewport;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, ObjCRuntime.Sel("setViewports:count:"), &value, 1);
        }

        /// <summary><c>-setScissorRects:count:</c> with one rectangle, same shape and same lifetime argument as
        /// <see cref="SetViewport"/>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetScissorRect(in MTLScissorRect rect)
        {
            MTLScissorRect value = rect;
            ObjCMsgSend.SendVoidPtrNUInt(Handle, ObjCRuntime.Sel("setScissorRects:count:"), &value, 1);
        }

        /// <summary>
        /// <c>-setVertexBuffers:offsets:withRange:</c> or <c>-setFragmentBuffers:offsets:withRange:</c>, over a
        /// CONTIGUOUS run of the stage's buffer table starting at <paramref name="firstIndex"/> (M-R6).
        /// <para>
        /// THE TWO SPANS ARE PINNED FOR THE CALL AND NOT BEYOND IT, which is the same lifetime argument
        /// <see cref="SetViewport"/> makes: Metal copies both arrays during the call and the encoder holds the
        /// bindings as its own state afterwards, so a caller's <c>stackalloc</c> is legal and a caller's pooled
        /// array may be reused the moment this returns.
        /// </para>
        /// </summary>
        /// <param name="stage">Which stage's table, and therefore which of the two selectors.</param>
        /// <param name="buffers">The <c>MTLBuffer</c> objects, one per index in the run. A nil entry unbinds its
        /// index, which is what a resource disposed since its set was created degrades to.</param>
        /// <param name="offsets">The composed byte offset for each, same length and same order.</param>
        /// <param name="firstIndex">The run's first argument-table index.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetBuffers(MetalShaderStage stage, ReadOnlySpan<IntPtr> buffers,
            ReadOnlySpan<nuint> offsets, uint firstIndex)
        {
            IntPtr selector = ObjCRuntime.Sel(Stage(stage, "setVertexBuffers:offsets:withRange:",
                "setFragmentBuffers:offsets:withRange:"));

            fixed (IntPtr* objects = buffers)
            fixed (nuint* offsetValues = offsets)
            {
                ObjCMsgSend.SendVoidBuffersRange(Handle, selector, objects, offsetValues,
                    new NSRange(firstIndex, (nuint)buffers.Length));
            }
        }

        /// <summary><c>-setVertexTextures:withRange:</c> or <c>-setFragmentTextures:withRange:</c>. Same run and
        /// same lifetime rule as <see cref="SetBuffers"/>, with no offsets array because the texture table binds
        /// no window.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetTextures(MetalShaderStage stage, ReadOnlySpan<IntPtr> textures, uint firstIndex)
        {
            IntPtr selector = ObjCRuntime.Sel(
                Stage(stage, "setVertexTextures:withRange:", "setFragmentTextures:withRange:"));

            fixed (IntPtr* objects = textures)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, selector, objects,
                    new NSRange(firstIndex, (nuint)textures.Length));
            }
        }

        /// <summary><c>-setVertexSamplerStates:withRange:</c> or <c>-setFragmentSamplerStates:withRange:</c>.
        /// The two-argument form deliberately, not the one carrying LOD clamps: the seam has no per-bind clamp
        /// and the sampler already carries its own from its descriptor.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void SetSamplerStates(MetalShaderStage stage, ReadOnlySpan<IntPtr> samplers,
            uint firstIndex)
        {
            IntPtr selector = ObjCRuntime.Sel(
                Stage(stage, "setVertexSamplerStates:withRange:", "setFragmentSamplerStates:withRange:"));

            fixed (IntPtr* objects = samplers)
            {
                ObjCMsgSend.SendVoidObjectsRange(Handle, selector, objects,
                    new NSRange(firstIndex, (nuint)samplers.Length));
            }
        }

        /// <summary>
        /// <c>-setVertexBufferOffset:atIndex:</c> or <c>-setFragmentBufferOffset:atIndex:</c> (M-R7), which
        /// moves an EXISTING binding's window without rewriting the argument-table entry behind it.
        /// <para>
        /// A BUFFER MUST ALREADY BE BOUND AT <paramref name="index"/>. There is nothing for this call to adjust
        /// otherwise, and the flush reaches it only for a slot whose set it already wrote into this encoder's
        /// table, which is that precondition expressed as the arm's own guard rather than as a comment.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetBufferOffset(MetalShaderStage stage, nuint offset, uint index)
            => ObjCMsgSend.SendVoidNUIntNUInt(
                Handle,
                ObjCRuntime.Sel(Stage(stage, "setVertexBufferOffset:atIndex:",
                    "setFragmentBufferOffset:atIndex:")),
                offset,
                index);

        // THE ONE PLACE THE STAGE BECOMES A SELECTOR, so a new setter added later cannot spell the fork a second
        // way. Compute is refused rather than folded into the vertex arm: a compute encoder is a different
        // protocol with unprefixed selectors, and sending a vertex selector to one is an unrecognised-selector
        // crash at best and a bind onto the wrong table at worst.
        static string Stage(MetalShaderStage stage, string vertex, string fragment) => stage switch
        {
            MetalShaderStage.Vertex => vertex,
            MetalShaderStage.Fragment => fragment,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "An MTLRenderCommandEncoder has a vertex stage and a fragment stage and nothing else. The "
                + "compute argument-table setters are unprefixed selectors on MTLComputeCommandEncoder, so this "
                + "is a bind routed to the wrong encoder kind rather than a stage this type could serve."),
        };
    }
}
