using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLComputePipelineState</c> handle. Arrives at +1 from
    /// <c>-newComputePipelineStateWithFunction:error:</c> and is released once by its wrapper.
    ///
    /// <para><b>THERE IS NO <c>MTLComputePipelineDescriptor</c> IN THIS FOLDER, AND ITS ABSENCE IS AN ARGUMENT
    /// RATHER THAN A GAP.</b> Metal offers two routes to this object: from a function, and from a descriptor
    /// carrying that function plus per-buffer MUTABILITY hints.
    /// <c>Veldrid.MTL.MTLPipeline</c> takes the descriptor route, through
    /// <c>-newComputePipelineStateWithDescriptor:options:reflection:error:</c>, and fills
    /// <c>buffers[bufferIndex].mutability</c> by walking the resource layouts in declaration order, incrementing
    /// its own counter per buffer-kind element. That counter is the per-kind declaration-order arithmetic section
    /// 2.2b forbids on a shipped path: it is the belief that a resource landed where the CPU counted it to, and
    /// the whole reason this backend reads the emitted index out of the MSL instead. Reproducing it would mark
    /// the mutability of whichever buffer happened to sit at the counted index, which is wrong exactly when the
    /// emission's order and the declaration's order differ.</para>
    ///
    /// <para><b>AND THE ARGUMENT FOR NOT SETTING IT AT ALL IS ABOUT WHAT THE TWO VALUES MEAN, WHICH IS WHY IT
    /// NEEDS NO EVIDENCE THIS REPO DOES NOT HAVE.</b> <c>MTLMutabilityDefault</c> is a DEFERRAL: it tells Metal
    /// to take the mutability from the function's own qualifiers, so whatever the compiler emitted is what the
    /// pipeline gets and there is no second claim to be wrong about. <c>MTLMutabilityImmutable</c> is an
    /// ASSERTION about that same emission, and an assertion the emission is free to contradict, which is exactly
    /// what the counter above makes it do. A deferral cannot disagree with the thing it defers to, so the
    /// function route is not merely as good as the descriptor route here, it is the arm that removes a failure
    /// mode. Taking it drops the descriptor, the buffer-descriptor array and the counter in one move.</para>
    ///
    /// <para>
    /// WHAT IS DELIBERATELY NOT CLAIMED is which qualifier SPIRV-Cross emits for which kind. The natural
    /// argument (a uniform emits as <c>constant</c>, a read-only storage buffer as <c>const device</c>, a
    /// read-write one as plain <c>device</c>, so the inference and the incumbent's table agree) rests on
    /// knowledge of the cross-compiler that nothing in this repo can cite: no emitted MSL is committed here, so
    /// there is no artifact a reader could check it against. The deferral argument above needs none of it and is
    /// what this decision rests on.
    /// </para>
    ///
    /// <para><b>WHAT WOULD REOPEN IT.</b> A reason to set something the function cannot imply: a
    /// <c>maxTotalThreadsPerThreadgroup</c> cap, or
    /// <c>threadGroupSizeIsMultipleOfThreadExecutionWidth</c>. Neither has a seam member behind it today, and
    /// section 8.4 declines Metal-native capabilities with no seam member for that reason.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLComputePipelineState(IntPtr Handle)
    {
        /// <summary>True when the device would not make a compute pipeline state, which always comes with an
        /// <c>NSError</c>.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this pipeline state.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }
}
