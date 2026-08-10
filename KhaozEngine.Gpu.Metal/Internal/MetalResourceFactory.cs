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
    /// around this row so it can parallelise with the command list. <see cref="CreateCommandList"/> is live too,
    /// and it is the one member here the resource row does not own: row 7 built the list and this hands it out.</para>
    ///
    /// <para><b>AND SHADERS ARE LIVE AS OF ROW 9</b> (https://github.com/APKiwiOrg/KhaozEngine/issues/575), which
    /// is the row that had to land before pipelines because a pipeline is created from a library and a function.
    /// Both shader members run the whole device-free path (GLSL to SPIR-V, SPIR-V to MSL under the pin, the
    /// entry-point read, the id-keyed binding join) and then make two native calls.</para>
    ///
    /// <para><b>LAYOUTS AND SETS ARE LIVE AS OF ROW 10</b> (https://github.com/APKiwiOrg/KhaozEngine/issues/576),
    /// and they are the two members here that touch NOTHING native at all: Metal has no layout object and this
    /// backend declines argument buffers (M-B6), so both are resolved managed data and both bodies are plain. What
    /// still refuses is the pipeline that binds through them and the framebuffer it draws into.</para>
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

        MetalShaderCompiler? _shaders;

        internal MetalResourceFactory(MetalGpuDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            _device = device;
        }

        /// <inheritdoc/>
        /// <remarks>Shared storage, and M-M6's creation refusal for a buffer that is both a uniform and a
        /// structured buffer. A <c>UniformBuffer</c>-usage buffer is cut into the device's ring segments
        /// (M-M3) and everything else takes the incumbent's four-byte size rounding. See
        /// <see cref="MetalBufferPolicy"/> and <see cref="MetalRingStride"/>.</remarks>
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
        /// <remarks>
        /// A COPIED ELEMENT ARRAY AND NOTHING ELSE. Metal has no layout object to create, and the per-kind
        /// declaration-order arithmetic the incumbent's <c>MTLResourceLayout</c> carries is deliberately absent
        /// (2.2b): the authority for where a resource landed is <see cref="MetalShaderIndexTable"/>. See
        /// <see cref="MetalResourceLayout"/>.
        /// <para>
        /// NO PLATFORM GUARD, unlike every member above it, because there is no native call to guard. The guards
        /// exist so CA1416 can see the macOS boundary at an <c>objc_msgSend</c> site, and a layout reaches none.
        /// </para>
        /// </remarks>
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
            => new MetalResourceLayout(_device.Liveness, d);

        /// <inheritdoc/>
        /// <remarks>Every element resolved ONCE, here, into the handle and the three numbers a bind writes. No
        /// native call and no argument buffer (M-B6). See <see cref="MetalResourceSet"/>.</remarks>
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
            => new MetalResourceSet(_device.Liveness, d);

        /// <inheritdoc/>
        /// <remarks>
        /// GLSL in, an <c>MTLLibrary</c> and an <c>MTLFunction</c> per stage out, plus the binding table read off
        /// the emission (M-B1, section 2.2b). Everything except the two native calls is device-free and asserted
        /// over every shipped program on every leg. See <see cref="MetalShaderBuild"/> and
        /// <see cref="MetalShaderIndexTable"/>.
        /// </remarks>
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU shader set");
            return CreateShaderSetOnMacOs(vertGlsl, fragGlsl);
        }

        /// <inheritdoc/>
        /// <remarks>The compute sibling, with the workgroup size read out of the SPIR-V module rather than taken
        /// from a description nothing validates.</remarks>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU compute shader");
            return CreateComputeShaderOnMacOs(computeGlsl);
        }

        /// <inheritdoc/>
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
            => throw NotBuiltYet("A graphics pipeline", PipelinesRow);

        /// <inheritdoc/>
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
            => throw NotBuiltYet("A compute pipeline", PipelinesRow);

        /// <inheritdoc/>
        /// <remarks>
        /// THE SEAM'S ONLY ROUTE TO A LIST, and the row 6 and row 7 join. Row 6 built this factory with the
        /// member refusing and naming row 7, and row 7 built <c>MetalGpuDevice.CreateCommandList</c> saying this
        /// is what would call it. It does, so a consumer holding an <c>IGpuDevice</c> can record and submit
        /// without naming anything internal, which is what makes the two rows one feature rather than two halves.
        /// <para>
        /// The list is created by the DEVICE rather than assembled here, because a list needs the device's
        /// command-buffer source, its uncommitted-buffer counter and the device itself as the owner token the
        /// submit path compares by reference, and a factory that reached for all three would be the second place
        /// that knowledge lived.
        /// </para>
        /// </remarks>
        public IGpuCommandList CreateCommandList()
        {
            if (!KhaozEngineMetal.IsPlatformSupported) throw OffMacOs("A GPU command list");
            return _device.CreateCommandList();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuBuffer CreateBufferOnMacOs(in GpuBufferDescription d)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            return MetalBuffer.Create(_device.Handle, _device.Liveness, d, _device.Rings);
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

        // No pool at these two, unlike every other body here, and that is not an oversight: the compiler opens
        // one around the native half itself, because the device-free emission in front of it is the expensive
        // part and holding a pool across four seconds of glslang and SPIRV-Cross would be holding it for nothing.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuShaderSet CreateShaderSetOnMacOs(string vertGlsl, string fragGlsl)
            => Shaders.CreateShaderSet(vertGlsl, fragGlsl);

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        IGpuComputeShader CreateComputeShaderOnMacOs(string computeGlsl)
            => Shaders.CreateComputeShader(computeGlsl);

        // Created on first use rather than in the constructor, so a device that never compiles a shader never
        // builds one, and so the factory's constructor stays free of anything that could throw.
        //
        // AND THE LAZY IS UNSYNCHRONIZED ON A PATH THIS TYPE'S HEADER CALLS FREE-THREADED, which is a benign race
        // rather than a hole in that claim. Two threads reaching this at once both construct, both assign, and
        // the loser's instance is dropped for the GC: the reference assignment is atomic so no caller can see a
        // half-built one, the constructor allocates nothing native and holds no handle it would have to release,
        // and the compiler itself is stateless (a device, a liveness latch and the DEVICE's index-table cache,
        // all three already shared). The cache being the device's is what keeps that true now that a compiler
        // carries one: a per-compiler cache would let this race split the dedup in two, and two instances of one
        // table would make M-R9's handle compare answer "different" for two pipelines that map every element
        // identically. A lock here would buy one avoided allocation, once, at the cost of putting a lock on the
        // path M-W8 says has none.
        [SupportedOSPlatform("macos")]
        MetalShaderCompiler Shaders =>
            _shaders ??= new MetalShaderCompiler(_device.Handle, _device.Liveness, _device.IndexTables);

        // The off-macOS refusal, as one sentence rather than as a silent null. A factory reached off macOS means
        // a consumer got hold of a device that cannot exist there, so the message says which of the two
        // impossible things happened rather than throwing with no context.
        static PlatformNotSupportedException OffMacOs(string what)
            => new(what + " cannot be created on the native Metal backend off macOS. Reaching this means a "
                + "MetalGpuDevice exists on a machine that has no Metal at all, which the provider's functional "
                + "probe refuses before creation.");

        const string PipelinesRow = "the pipeline row (https://github.com/APKiwiOrg/KhaozEngine/issues/577)";
        const string PassesRow = "the render-pass row (https://github.com/APKiwiOrg/KhaozEngine/issues/578)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not, in
        // the shape MetalGpuDevice settled on: a reader who hits this needs to know whether the backend is
        // unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend's resource factory: it lands in {row}. "
                + "Buffers, textures, samplers and fences ARE live (work-breakdown row 6, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/572), so are command lists (row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/573), shader sets and compute shaders (row 9, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/575), and resource layouts and resource sets "
                + "(row 10, https://github.com/APKiwiOrg/KhaozEngine/issues/576). This is a statement about the "
                + "package and not about this machine. Select GpuBackendKind.Metal, which goes through Veldrid, "
                + "for a fully working Metal device.");
    }
}
