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
    /// NOT EVERY MEMBER IS BUILT YET. Shader compilation, compute pipelines and fences are separate rows of the
    /// same program, and each throws a message naming what is missing rather than returning something that would
    /// fail later somewhere less informative. The members this row owns are live.
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
        /// </summary>
        internal D3D11ResourceFactory(ID3D11Device device, ID3D11DeviceContext context,
            D3D11DeviceLiveness liveness, D3D11RingAllocator rings, Func<IGpuCommandList> createCommandList)
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
        }

        /// <inheritdoc/>
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
            => new D3D11Buffer(_device, _context, _liveness, _rings, d);

        /// <inheritdoc/>
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => new D3D11Texture(_device, _liveness, d);

        /// <inheritdoc/>
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => new D3D11Sampler(_device, _liveness, d);

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
            => new D3D11GraphicsPipeline(_device, _liveness, d);

        /// <inheritdoc/>
        public IGpuCommandList CreateCommandList() => _createCommandList();

        /// <inheritdoc/>
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => throw NotBuiltYet(
            "Cross-compiling GLSL to HLSL and calling FXC");

        /// <inheritdoc/>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => throw NotBuiltYet(
            "Cross-compiling a compute kernel and calling FXC");

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
