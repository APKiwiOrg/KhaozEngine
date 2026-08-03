using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuResourceFactory"/> for the native Direct3D 11 backend: the one place a native resource is
    /// created, and therefore the one place decision X1's eager views and decision X3's liveness token are handed
    /// out.
    /// <para>
    /// EVERY CREATION IS EAGER AND COMPLETE. A buffer arrives with its views made, a texture with all of its, a
    /// pipeline with its four state objects and its input layout, and a resource set with every buffer window
    /// already resolved. Nothing here defers work to the draw path, which is what makes the emitter seam's absence
    /// of a <c>Create*</c> member enforceable rather than aspirational.
    /// </para>
    /// <para>
    /// EVERY MEMBER IS LIVE ON A DEVICE. <see cref="CreateFence"/> is the one that comes from somewhere else: the
    /// fence subsystem owns the timeline, so the device hands its factory that subsystem's own creation call.
    /// A factory built WITHOUT one (which is every device-free test, since none of them has a timeline) still
    /// refuses by name rather than returning something that would fail later somewhere less informative.
    /// </para>
    /// <para>
    /// SHADER COMPILATION IS EAGER TOO, and eager here means more than it does elsewhere: a shader set arrives
    /// cross-compiled, FXC-compiled, checked for the holed-signature hazard of decision S5 and bound to the
    /// device, with its vertex bytecode kept so a pipeline can validate an input layout against it later.
    /// </para>
    /// <para>
    /// CREATION IS FREE-THREADED, BEHIND <see cref="D3D11CreationGate"/> WHEN THE DRIVER IS NOT (decision W4).
    /// Every member below that makes a native creation call takes the gate for the duration of that call and
    /// nothing longer, which is a no-op on a driver reporting <c>DriverConcurrentCreates</c> and one uncontended
    /// monitor otherwise. FIVE MEMBERS ARE UNGATED, IN TWO GROUPS. Four are live and create NO native object:
    /// <see cref="CreateFramebuffer"/> aggregates views that already exist,
    /// <see cref="CreateResourceLayout"/> and <see cref="CreateResourceSet"/> are pure engine data, and
    /// <see cref="CreateCommandList"/> hands back a recorder that touches no device state at all (which is the
    /// same clause of W4 the recording model rests on). Gating those would serialize engine work behind a driver
    /// limitation that has nothing to do with it. The fifth, <see cref="CreateFence"/>, creates no native object
    /// either: the device's ONE timeline object was created with the device, and a fence on this backend is an
    /// engine-side target against it, so there is nothing for a creation gate to serialize.
    /// <see cref="CreateComputePipeline"/> creates no native object either and IS gated anyway, for the reason
    /// stated on it.
    /// </para>
    /// <para>
    /// THE GATE NEVER TAKES THE SUBMIT LOCK, and no creation path here reaches it either: the ring is only asked
    /// for its segment count, and a ring-backed buffer's mapping is taken later, by the first write. That is what
    /// keeps the two locks a strict outer and leaf pair rather than a cycle. See
    /// <see cref="D3D11CreationGate"/> for the ordering rule in full.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ResourceFactory : IGpuResourceFactory
    {
        readonly ID3D11Device _device;
        readonly ID3D11DeviceContext _context;
        readonly D3D11DeviceLiveness _liveness;
        readonly D3D11RingAllocator _rings;
        readonly Func<IGpuCommandList> _createCommandList;
        readonly D3D11CreationGate _creation;
        readonly D3D11ShaderCompiler _shaders;
        readonly Func<IGpuFence>? _createFence;
        readonly D3D11DeviceLossLatch? _loss;
        readonly int _maxMsaaSampleCount;

        /// <summary>
        /// <paramref name="createCommandList"/> comes from the device rather than being built here, because which
        /// recording driver a list uses is the device's decision (it holds the real emitter and reads
        /// <c>KE_D3D11_RECORD</c>) and threading a generic emitter type through this factory would put that choice
        /// in every signature.
        /// <para>
        /// <paramref name="context"/> and <paramref name="rings"/> arrive for the same reason and are used by one
        /// member between them: a uniform buffer is ring-backed (decision U1), so it needs the segment count to
        /// size itself and the immediate context to map itself. Both belong to the device, which owns exactly one
        /// ring allocator.
        /// </para>
        /// <para>
        /// <paramref name="capabilities"/> is the device's own capability set, and exactly one member of it is
        /// read here: <see cref="GpuCapabilities.MaxMsaaSampleCount"/>, for decision C4's throw on
        /// <see cref="CreateTexture"/>. The WHOLE set is taken rather than that one number because a factory that
        /// has to validate against a second capability later should not need a signature change to see it.
        /// </para>
        /// <para>
        /// <paramref name="creation"/> is the device's too, and null means the device did not read the threading
        /// probe, which takes the serialized arm. That is the same conservative direction
        /// <see cref="D3D11CreationGate.For"/> takes for an unknown answer, restated here so a device row that
        /// simply does not pass one cannot silently opt into free-threaded creation on a driver that cannot do it.
        /// </para>
        /// <para>
        /// <paramref name="createFence"/> is the device's fence subsystem, for the same reason
        /// <paramref name="createCommandList"/> is the device's recording driver: the timeline belongs to the
        /// device and a factory that built its own would hand out fences against a second, unrelated one. Null is
        /// a factory with no device behind it, which refuses by name.
        /// </para>
        /// <para>
        /// <paramref name="loss"/> is the device's device-loss latch, and it travels through here for ONE
        /// destination: a ring-backed uniform buffer's mapping mechanism, whose <c>Map</c> is a G3 check site
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/500). Null skips the attribution and still throws.
        /// </para>
        /// </summary>
        internal D3D11ResourceFactory(ID3D11Device device, ID3D11DeviceContext context,
            D3D11DeviceLiveness liveness, D3D11RingAllocator rings, Func<IGpuCommandList> createCommandList,
            in GpuCapabilities capabilities, D3D11CreationGate? creation = null,
            Func<IGpuFence>? createFence = null, D3D11DeviceLossLatch? loss = null)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(rings);
            ArgumentNullException.ThrowIfNull(createCommandList);

            _device = device;
            _context = context;
            _liveness = liveness;
            _rings = rings;
            _createCommandList = createCommandList;
            _maxMsaaSampleCount = capabilities.MaxMsaaSampleCount;
            _creation = creation ?? D3D11CreationGate.For(null);
            _createFence = createFence;
            _loss = loss;
            // Built here rather than passed in, because a shader compiler is a pure function of the device plus
            // two environment levers it reads ONCE (the FXC flags and the disk cache location, decisions S1 and
            // S4). Threading it through every construction path would put a session-level setting in a signature.
            _shaders = new D3D11ShaderCompiler(device, liveness);
        }

        /// <summary>Whether creation is serialized on this driver, for the session log. See
        /// <see cref="D3D11CreationGate"/>.</summary>
        internal bool SerializesCreation => _creation.Serializes;

        /// <inheritdoc/>
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
        {
            using (_creation.Enter()) return new D3D11Buffer(_device, _context, _liveness, _rings, d, _loss);
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// DECISION C4: <see cref="GpuTextureDescription.SampleCount"/> is above this device's
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/>. It THROWS rather than rounding down, because the
        /// engine already has the one place a request is meant to be clamped
        /// (<c>AntiAliasing.ResolveFor</c> in KhaozEngine.Render3D), so a count arriving here above the maximum
        /// came from a caller that skipped it, and rounding down would hide that behind a framebuffer that is
        /// quietly not multisampled.
        /// <para>
        /// The check is HERE rather than in <see cref="D3D11Texture"/> because this is where the device's
        /// capabilities are known, and because the swapchain builds its own depth attachment through that
        /// constructor directly at a single sample, which is engine-controlled and has nothing to validate.
        /// The validation runs BEFORE the creation gate is entered, since it is pure engine logic and holding
        /// the gate across it would serialize a check that touches no driver.
        /// </para>
        /// </exception>
        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
            string? unsupported = D3D11CapabilityRead.UnsupportedSampleCountMessage(d.SampleCount, _maxMsaaSampleCount);
            if (unsupported != null) throw new ArgumentException(unsupported, nameof(d));

            using (_creation.Enter()) return new D3D11Texture(_device, _liveness, d);
        }

        /// <inheritdoc/>
        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
        {
            using (_creation.Enter()) return new D3D11Sampler(_device, _liveness, d);
        }

        /// <inheritdoc/>
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            IGpuTexture[] colourTextures = colour ?? Array.Empty<IGpuTexture>();
            var native = new D3D11Texture[colourTextures.Length];
            for (int i = 0; i < native.Length; i++) native[i] = Require(colourTextures[i], "colour attachment");
            return new D3D11Framebuffer(depth is null ? null : Require(depth, "depth attachment"), native);
        }

        /// <inheritdoc/>
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
            => new D3D11ResourceLayout(d);

        /// <inheritdoc/>
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => new D3D11ResourceSet(d);

        /// <inheritdoc/>
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
        {
            using (_creation.Enter()) return new D3D11GraphicsPipeline(_device, _liveness, d);
        }

        /// <inheritdoc/>
        public IGpuCommandList CreateCommandList() => _createCommandList();

        /// <inheritdoc/>
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            using (_creation.Enter()) return _shaders.CreateShaderSet(vertGlsl, fragGlsl);
        }

        /// <inheritdoc/>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
        {
            using (_creation.Enter()) return _shaders.CreateComputeShader(computeGlsl);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// GATED LIKE ITS GRAPHICS SIBLING even though it creates no native object, and that is a deliberate
        /// exception to the ungated-when-nothing-native rule above rather than an oversight. The two pipeline
        /// members are the pair a reader compares, the gate is a no-op on a driver that reports
        /// <c>DriverConcurrentCreates</c> and one uncontended monitor otherwise, and a compute pipeline is the
        /// member most likely to grow a native call later (a reflected signature check of the kind decision S5
        /// puts on the graphics side). Paying an uncontended monitor at load time to keep the pair symmetric is
        /// the cheaper mistake to make.
        /// </remarks>
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
        {
            using (_creation.Enter()) return new D3D11ComputePipeline(d);
        }

        /// <summary>
        /// A fresh, unarmed completion fence, from the DEVICE's one timeline. There is no capability gate in
        /// front of it, unlike the Veldrid factory's, because this backend's
        /// <see cref="GpuCapabilities.SupportsCompletionFences"/> is unconditionally true on both timeline
        /// mechanisms (decision C5).
        /// </summary>
        /// <exception cref="NotSupportedException">This factory was built with no fence source, which means it
        /// has no device behind it. Every shipped path has one.</exception>
        public IGpuFence CreateFence()
            => (_createFence ?? throw NotBuiltYet("Completion fences"))();

        static D3D11Texture Require(IGpuTexture texture, string what)
            => texture as D3D11Texture
                ?? throw new ArgumentException(
                    $"The {what} was not created by the native Direct3D 11 backend, so it carries none of the "
                    + "views a framebuffer binds.", nameof(texture));

        static NotSupportedException NotBuiltYet(string what)
            => new($"{what} is not built yet on the native Direct3D 11 backend. Resources, resource layouts, "
                + "resource sets and graphics pipelines are live. Select GpuBackendKind.Direct3D11 for a fully "
                + "working Direct3D 11 device.");
    }
}
