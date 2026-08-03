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
    /// NOT EVERY MEMBER IS BUILT YET. Compute pipelines and fences are separate rows of the same program, and
    /// each throws a message naming what is missing rather than returning something that would fail later
    /// somewhere less informative. Everything else, including the shader path, is live.
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
    /// monitor otherwise. SIX MEMBERS ARE UNGATED, IN TWO GROUPS. Four are live and create NO native object:
    /// <see cref="CreateFramebuffer"/> aggregates views that already exist,
    /// <see cref="CreateResourceLayout"/> and <see cref="CreateResourceSet"/> are pure engine data, and
    /// <see cref="CreateCommandList"/> hands back a recorder that touches no device state at all (which is the
    /// same clause of W4 the recording model rests on). Gating those would serialize engine work behind a driver
    /// limitation that has nothing to do with it. The other two, <see cref="CreateComputePipeline"/> and
    /// <see cref="CreateFence"/>, are the members of the paragraph above that are not built yet: they throw
    /// before reaching any driver, so there is nothing to gate until the rows that build them land, and each
    /// takes the gate on the day it makes a native creation call.
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
        /// <paramref name="creation"/> is the device's too, and null means the device did not read the threading
        /// probe, which takes the serialized arm. That is the same conservative direction
        /// <see cref="D3D11CreationGate.For"/> takes for an unknown answer, restated here so a device row that
        /// simply does not pass one cannot silently opt into free-threaded creation on a driver that cannot do it.
        /// </para>
        /// </summary>
        internal D3D11ResourceFactory(ID3D11Device device, ID3D11DeviceContext context,
            D3D11DeviceLiveness liveness, D3D11RingAllocator rings, Func<IGpuCommandList> createCommandList,
            D3D11CreationGate? creation = null)
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
            _creation = creation ?? D3D11CreationGate.For(null);
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
            using (_creation.Enter()) return new D3D11Buffer(_device, _context, _liveness, _rings, d);
        }

        /// <inheritdoc/>
        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
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
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw NotBuiltYet(
            "Compute pipelines");

        /// <inheritdoc/>
        public IGpuFence CreateFence() => throw NotBuiltYet("Completion fences");

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
