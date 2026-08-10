using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S <see cref="IGpuResourceFactory"/>, filled to the members the RESOURCE row owns and refusing the
    /// rest by naming the row that builds each.
    ///
    /// <para><b>WHAT IS LIVE HERE IS BUFFERS, TEXTURES, SAMPLERS AND FENCES.</b> Every one of those is a plain
    /// object with no encoder, no pipeline and no shader behind it, which is exactly the boundary section 18 draws
    /// around this row so it can parallelise with the command list.</para>
    ///
    /// <para><b>CREATION IS FREE-THREADED AND TAKES NO LOCK AT ALL (M-W8).</b> An <c>MTLDevice</c> is documented
    /// thread-safe and none of these calls touches shared state: the setup command buffer is the only thing in
    /// this row with a lock, and creation never appends to it because texture creation issues no command buffer
    /// (M-M9).</para>
    ///
    /// <para><b>EVERY BODY OPENS AN AUTORELEASE POOL (M-N5).</b> A descriptor is an autoreleased object in every
    /// case where it is not created through <c>alloc</c>, and the class lookups underneath these calls return
    /// autoreleased objects too. <c>MetalAutoreleaseArchitectureTests</c> walks the IL rather than trusting this
    /// paragraph.</para>
    /// </summary>
    internal sealed class MetalResourceFactory : IGpuResourceFactory
    {
        readonly MetalGpuDevice _device;

        internal MetalResourceFactory(MetalGpuDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            _device = device;
        }

        /// <inheritdoc/>
        /// <remarks>Shared storage, the incumbent's four-byte size rounding, and M-M6's creation refusal for a
        /// buffer that is both a uniform and a structured buffer. See <see cref="MetalBufferPolicy"/>.</remarks>
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
        {
            // The guard is INLINE at every one of these three sites rather than factored into a helper, because
            // CA1416 reads the guard property at the call site and a helper hides it. Warnings are errors here,
            // so the compiler is what keeps the macOS boundary rather than a convention (M-P1).
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU buffer");
            return CreateBufferOnMacOs(d);
        }

        /// <inheritdoc/>
        /// <remarks>A Private <c>MTLTexture</c>, or a Shared <c>MTLBuffer</c> for a staging texture (M-M2, M-C5).
        /// No command buffer is issued and no view is created (M-M9, M-M10).</remarks>
        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU texture");
            return CreateTextureOnMacOs(d);
        }

        /// <inheritdoc/>
        /// <remarks>Every value the seam does not expose is hardcoded to what the incumbent hardcodes, and the
        /// incumbent's two conditionals are resolved rather than reproduced. See
        /// <see cref="MetalSamplerPolicy"/>.</remarks>
        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU sampler");
            return CreateSamplerOnMacOs(d);
        }

        /// <inheritdoc/>
        /// <remarks>No capability gate in front of it, because <c>SupportsCompletionFences</c> is unconditionally
        /// true on this backend: the device timeline is one <c>MTLSharedEvent</c> and a fence is a target on
        /// it.</remarks>
        public IGpuFence CreateFence() => _device.Timeline.CreateFence();

        /// <inheritdoc/>
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
            => throw NotBuiltYet("A framebuffer", PassesRow);

        /// <inheritdoc/>
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
            => throw NotBuiltYet("A resource layout", LayoutsRow);

        /// <inheritdoc/>
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
            => throw NotBuiltYet("A resource set", LayoutsRow);

        /// <inheritdoc/>
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
            => throw NotBuiltYet("A shader set", ShaderRow);

        /// <inheritdoc/>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
            => throw NotBuiltYet("A compute shader", ShaderRow);

        /// <inheritdoc/>
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
            => throw NotBuiltYet("A graphics pipeline", PipelinesRow);

        /// <inheritdoc/>
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
            => throw NotBuiltYet("A compute pipeline", PipelinesRow);

        /// <inheritdoc/>
        public IGpuCommandList CreateCommandList() => throw NotBuiltYet("A command list", CommandListRow);

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuBuffer CreateBufferOnMacOs(in GpuBufferDescription d)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return MetalBuffer.Create(_device.Handle, _device.Liveness, d);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuTexture CreateTextureOnMacOs(in GpuTextureDescription d)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return MetalTexture.Create(_device.Handle, _device.Liveness, d);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuSampler CreateSamplerOnMacOs(in GpuSamplerDescription d)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return MetalSampler.Create(_device.Handle, _device.Liveness, d);
        }

        // The off-macOS refusal, as one sentence rather than as a silent null. A factory reached off macOS means
        // a consumer got hold of a device that cannot exist there, so the message says which of the two
        // impossible things happened rather than throwing with no context.
        static PlatformNotSupportedException OffMacOs(string what)
            => new(what + " cannot be created on the native Metal backend off macOS. Reaching this means a "
                + "MetalGpuDevice exists on a machine that has no Metal at all, which the provider's functional "
                + "probe refuses before creation.");

        const string CommandListRow = "the command-list row (https://github.com/APKiwiOrg/KhaozEngine/issues/573)";
        const string ShaderRow = "the shader-path row (https://github.com/APKiwiOrg/KhaozEngine/issues/575)";
        const string LayoutsRow = "the resource-layout row (https://github.com/APKiwiOrg/KhaozEngine/issues/576)";
        const string PipelinesRow = "the pipeline row (https://github.com/APKiwiOrg/KhaozEngine/issues/577)";
        const string PassesRow = "the render-pass row (https://github.com/APKiwiOrg/KhaozEngine/issues/578)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not, in
        // the shape MetalGpuDevice settled on: a reader who hits this needs to know whether the backend is
        // unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend's resource factory: it lands in {row}. "
                + "Buffers, textures, samplers and fences ARE live (work-breakdown row 6, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/572). This is a statement about the package "
                + "and not about this machine. Select GpuBackendKind.Metal, which goes through Veldrid, for a "
                + "fully working Metal device.");
    }
}
