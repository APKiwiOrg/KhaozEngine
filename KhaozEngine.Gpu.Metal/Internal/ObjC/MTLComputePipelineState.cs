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
    /// <c>Veldrid.MTL.MTLPipeline</c> takes the descriptor route and fills
    /// <c>buffers[bufferIndex].mutability</c> by walking the resource layouts in declaration order, incrementing
    /// its own counter per buffer-kind element. That counter is the per-kind declaration-order arithmetic section
    /// 2.2b forbids on a shipped path: it is the belief that a resource landed where the CPU counted it to, and
    /// the whole reason this backend reads the emitted index out of the MSL instead. Reproducing it would mark
    /// the mutability of whichever buffer happened to sit at the counted index, which is wrong exactly when the
    /// emission's order and the declaration's order differ.</para>
    ///
    /// <para><b>AND THE HINT IT SETS IS ALREADY IMPLIED BY THE SHADER, so nothing is lost by not setting
    /// it.</b> <c>MTLMutabilityDefault</c> means "infer from the function's own declaration", and SPIRV-Cross
    /// emits a read-only storage buffer as <c>const device</c> and a uniform as <c>constant</c>, which is exactly
    /// the immutable the incumbent asserts, and a read-write storage buffer as plain <c>device</c>, which is
    /// exactly its mutable. So the inference and the incumbent's table agree wherever the incumbent's counter is
    /// right, and the inference is still right where the counter is not. Taking the function route drops the
    /// descriptor, the buffer-descriptor array and the counter in one move.</para>
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
