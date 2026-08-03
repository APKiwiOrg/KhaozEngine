using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>What a resource is bindable as, engine-side. A one-to-one mirror of the Direct3D 11 bind flags,
    /// kept as an engine enum so the DERIVATION from the seam's usage bits is a pure function that can be tested
    /// without a device, on any operating system. <see cref="D3D11Formats"/> turns it into the real flags.</summary>
    [Flags]
    internal enum D3D11BindUsage
    {
        /// <summary>Bindable as nothing. A staging resource, which is CPU-mapped and never bound.</summary>
        None = 0,
        /// <summary>Bindable as a vertex buffer.</summary>
        VertexBuffer = 1 << 0,
        /// <summary>Bindable as an index buffer.</summary>
        IndexBuffer = 1 << 1,
        /// <summary>Bindable as a constant buffer.</summary>
        ConstantBuffer = 1 << 2,
        /// <summary>Bindable as a shader resource, the <c>t</c> file.</summary>
        ShaderResource = 1 << 3,
        /// <summary>Bindable as an unordered access view, the <c>u</c> file.</summary>
        UnorderedAccess = 1 << 4,
        /// <summary>Bindable as a colour render target.</summary>
        RenderTarget = 1 << 5,
        /// <summary>Bindable as a depth-stencil attachment.</summary>
        DepthStencil = 1 << 6,
    }

    /// <summary>
    /// The eager views a texture gets, decided from its declared usage bits alone. At most FOUR, and the bound is
    /// real rather than optimistic: see <see cref="D3D11ViewPolicy"/> for why the seam cannot ask for a fifth.
    /// </summary>
    internal readonly struct D3D11TextureViewPlan
    {
        internal D3D11TextureViewPlan(bool shaderResource, bool renderTarget, bool depthStencil,
            bool unorderedAccess, D3D11BindUsage bind, bool staging)
        {
            ShaderResource = shaderResource;
            RenderTarget = renderTarget;
            DepthStencil = depthStencil;
            UnorderedAccess = unorderedAccess;
            Bind = bind;
            Staging = staging;
        }

        /// <summary>A shader resource view over the FULL mip chain and every array layer.</summary>
        internal bool ShaderResource { get; }
        /// <summary>A render target view at mip 0, layer 0.</summary>
        internal bool RenderTarget { get; }
        /// <summary>A depth-stencil view at mip 0, layer 0.</summary>
        internal bool DepthStencil { get; }
        /// <summary>An unordered access view at mip 0.</summary>
        internal bool UnorderedAccess { get; }
        /// <summary>The Direct3D bind flags the texture resource itself is created with.</summary>
        internal D3D11BindUsage Bind { get; }
        /// <summary>CPU-mapped readback texture: no bind flags, no views, <c>ResourceUsage.Staging</c>.</summary>
        internal bool Staging { get; }

        /// <summary>How many view objects this plan creates. Never more than four.</summary>
        internal int ViewCount =>
            (ShaderResource ? 1 : 0) + (RenderTarget ? 1 : 0) + (DepthStencil ? 1 : 0) + (UnorderedAccess ? 1 : 0);
    }

    /// <summary>The eager views a buffer gets, plus how the buffer resource itself is created.</summary>
    internal readonly struct D3D11BufferViewPlan
    {
        internal D3D11BufferViewPlan(bool shaderResource, bool unorderedAccess, bool rawViews,
            D3D11BindUsage bind, bool dynamic, bool staging, bool indirect)
        {
            ShaderResource = shaderResource;
            UnorderedAccess = unorderedAccess;
            RawViews = rawViews;
            Bind = bind;
            Dynamic = dynamic;
            Staging = staging;
            Indirect = indirect;
        }

        /// <summary>A full-range RAW (byte-address) shader resource view. Both structured kinds get one.</summary>
        internal bool ShaderResource { get; }
        /// <summary>A full-range RAW unordered access view. Only the read-write structured kind gets one.</summary>
        internal bool UnorderedAccess { get; }
        /// <summary>The resource carries <c>BufferAllowRawViews</c>, which is what makes those views legal.</summary>
        internal bool RawViews { get; }
        /// <summary>The Direct3D bind flags the buffer resource is created with.</summary>
        internal D3D11BindUsage Bind { get; }
        /// <summary>CPU-writable dynamic buffer. No renderer asks for this today.</summary>
        internal bool Dynamic { get; }
        /// <summary>CPU-mapped readback buffer.</summary>
        internal bool Staging { get; }
        /// <summary>Indirect draw-argument buffer, which needs its own misc flag.</summary>
        internal bool Indirect { get; }
    }

    /// <summary>
    /// DECISION X1, THE EAGER-VIEW POLICY, AS ENGINE LOGIC. Which views follow from which usage bits is decided
    /// here, without a device, so the rule is a testable function rather than a shape buried in a constructor that
    /// only runs on Windows. Creating the actual view objects is the Windows-boundary half, in the wrapper types.
    /// <para>
    /// WHY EAGER AT ALL. Every one of the 25 <c>DEVICE_REMOVED</c> stacks the incumbent produced in the field
    /// surfaced inside a texture-view constructor reached from resource-set activation. Lazy view creation put an
    /// allocation on the draw path and put it on precisely the path a corrupted context makes fail, so the fault
    /// landed far from its cause and looked like a rendering bug. Creating every view at resource creation moves
    /// that work to load time. The emitter seam has NO <c>Create*</c> member, so a draw-time view creation is a
    /// compile error rather than a rule someone has to remember.
    /// </para>
    /// <para>
    /// WHY FOUR IS ENOUGH, and this is a fact about the seam rather than optimism. The full mip chain and every
    /// array layer go in ONE shader resource view because nothing can ask for a sub-range: the seam has no texture
    /// view type at all. Mip 0 and layer 0 are enough for the render target and depth views because
    /// <c>CreateFramebuffer</c> takes bare textures with no mip and no layer parameter. Subresource 0 is enough for
    /// the resolve because <c>ResolveTexture</c> takes two textures and nothing else. And per-face cubemap
    /// rendering is not expressible, so no per-face render target view can be requested. Widening any of those is
    /// a seam change, and a seam change is where the extra view would be added.
    /// </para>
    /// <para>
    /// <see cref="GpuTextureUsage.GenerateMipmaps"/> earns a shader resource view alongside
    /// <see cref="GpuTextureUsage.Sampled"/> because <c>GenerateMips</c> is defined as reading and writing THROUGH
    /// a shader resource view. A texture asking for mip generation without asking to be sampled would otherwise
    /// have no view to generate through, and the call would fail at the point of use.
    /// </para>
    /// </summary>
    internal static class D3D11ViewPolicy
    {
        /// <summary>
        /// The eager view set and bind flags for a texture with <paramref name="usage"/>.
        /// <para>
        /// <see cref="GpuTextureUsage.Staging"/> is rejected in combination with anything else, rather than
        /// silently dropping the other bits. Direct3D 11 refuses a staging resource that carries bind flags, so the
        /// combination has no meaning to honour, and every staging texture the engine creates passes the bit alone.
        /// </para>
        /// </summary>
        internal static D3D11TextureViewPlan ForTexture(GpuTextureUsage usage)
        {
            bool staging = (usage & GpuTextureUsage.Staging) != 0;
            if (staging && usage != GpuTextureUsage.Staging)
            {
                throw new ArgumentException(
                    "A staging texture is CPU-mapped and cannot be bound, so GpuTextureUsage.Staging cannot be "
                    + "combined with any other usage. Direct3D 11 rejects a staging resource that carries bind "
                    + "flags outright. Read back by copying into a staging texture of its own.",
                    nameof(usage));
            }
            if (staging) return new D3D11TextureViewPlan(false, false, false, false, D3D11BindUsage.None, true);

            bool sampled = (usage & GpuTextureUsage.Sampled) != 0;
            bool mips = (usage & GpuTextureUsage.GenerateMipmaps) != 0;
            bool renderTarget = (usage & GpuTextureUsage.RenderTarget) != 0;
            bool depthStencil = (usage & GpuTextureUsage.DepthStencil) != 0;
            bool storage = (usage & GpuTextureUsage.Storage) != 0;

            D3D11BindUsage bind = D3D11BindUsage.None;
            if (sampled || mips) bind |= D3D11BindUsage.ShaderResource;
            // Mip generation writes each level as a render target, so the bit implies both flags on the resource
            // even when the caller never named RenderTarget.
            if (renderTarget || mips) bind |= D3D11BindUsage.RenderTarget;
            if (depthStencil) bind |= D3D11BindUsage.DepthStencil;
            if (storage) bind |= D3D11BindUsage.UnorderedAccess;

            return new D3D11TextureViewPlan(
                shaderResource: sampled || mips,
                renderTarget: renderTarget || mips,
                depthStencil: depthStencil,
                unorderedAccess: storage,
                bind,
                staging: false);
        }

        /// <summary>
        /// The eager view set and bind flags for a buffer with <paramref name="usage"/>.
        /// <para>
        /// DECISION C2 IS THE INTERESTING PART. Both structured kinds get a FULL-RANGE RAW (byte-address) view over
        /// a <c>DEFAULT</c>-usage buffer, and <see cref="GpuBufferDescription.StructureByteStride"/> stays advisory
        /// rather than shaping the view. That is not a simplification: SPIRV-Cross emits a GLSL storage block as a
        /// <c>ByteAddressBuffer</c> or <c>RWByteAddressBuffer</c>, so a structured view with a stride would not be
        /// what the compiled shader reads. The ocean compute kernels are the shipped proof, and keeping this
        /// identical to the incumbent is why they keep working.
        /// </para>
        /// <para>
        /// A read-only structured buffer takes the shader resource view alone, and a read-write one takes BOTH,
        /// because a read-write storage block is still readable.
        /// </para>
        /// </summary>
        internal static D3D11BufferViewPlan ForBuffer(GpuBufferUsage usage)
        {
            bool staging = (usage & GpuBufferUsage.Staging) != 0;
            if (staging && usage != GpuBufferUsage.Staging)
            {
                throw new ArgumentException(
                    "A staging buffer is CPU-mapped and cannot be bound, so GpuBufferUsage.Staging cannot be "
                    + "combined with any other usage. Direct3D 11 rejects a staging resource that carries bind "
                    + "flags outright. Read back by copying into a staging buffer of its own.",
                    nameof(usage));
            }

            bool structuredRead = (usage & GpuBufferUsage.StructuredBufferReadOnly) != 0;
            bool structuredWrite = (usage & GpuBufferUsage.StructuredBufferReadWrite) != 0;

            D3D11BindUsage bind = D3D11BindUsage.None;
            if (!staging)
            {
                if ((usage & GpuBufferUsage.VertexBuffer) != 0) bind |= D3D11BindUsage.VertexBuffer;
                if ((usage & GpuBufferUsage.IndexBuffer) != 0) bind |= D3D11BindUsage.IndexBuffer;
                if ((usage & GpuBufferUsage.UniformBuffer) != 0) bind |= D3D11BindUsage.ConstantBuffer;
                if (structuredRead || structuredWrite) bind |= D3D11BindUsage.ShaderResource;
                if (structuredWrite) bind |= D3D11BindUsage.UnorderedAccess;
            }

            return new D3D11BufferViewPlan(
                shaderResource: structuredRead || structuredWrite,
                unorderedAccess: structuredWrite,
                rawViews: structuredRead || structuredWrite,
                bind,
                dynamic: !staging && (usage & GpuBufferUsage.Dynamic) != 0,
                staging,
                indirect: !staging && (usage & GpuBufferUsage.IndirectBuffer) != 0);
        }
    }
}
