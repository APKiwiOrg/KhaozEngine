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
            D3D11BindUsage bind, bool dynamic, bool staging, bool indirect, bool ring)
        {
            ShaderResource = shaderResource;
            UnorderedAccess = unorderedAccess;
            RawViews = rawViews;
            Bind = bind;
            Dynamic = dynamic;
            Staging = staging;
            Indirect = indirect;
            Ring = ring;
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
        /// <summary>
        /// RING-BACKED (decision U1): a uniform buffer, so the resource is created <c>DYNAMIC</c> plus
        /// <c>CPU_ACCESS_WRITE</c> at its 256-aligned size times the frame count, and every write goes through the
        /// mapped segment rather than through <c>UpdateSubresource</c>. True for exactly the
        /// <see cref="GpuBufferUsage.UniformBuffer"/> buffers, which decision U3's creation invariant makes an
        /// exclusive set.
        /// <para>
        /// It implies <see cref="Dynamic"/> and supersedes it. A caller that also passed
        /// <see cref="GpuBufferUsage.Dynamic"/> gets the same resource either way, since the ring is the dynamic
        /// path, and no renderer passes that bit at all.
        /// </para>
        /// </summary>
        internal bool Ring { get; }
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
    /// have no view to generate through, and the call would fail at the point of use. It earns the render target
    /// BIND FLAG too, which Direct3D 11 requires on the resource, but NO render target view: the eager view
    /// follows <see cref="GpuTextureUsage.RenderTarget"/> alone, and one created for mip generation would be an
    /// object nothing ever binds.
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
            // A FLAG, NOT A VIEW. GenerateMips writes each level as a render target internally, so Direct3D 11
            // requires the render target bind flag on the resource even when the caller never named RenderTarget.
            // The eager render target VIEW below follows RenderTarget alone, because nothing binds one for mip
            // generation and an unbound view is an object to keep alive for no reason.
            if (renderTarget || mips) bind |= D3D11BindUsage.RenderTarget;
            if (depthStencil) bind |= D3D11BindUsage.DepthStencil;
            if (storage) bind |= D3D11BindUsage.UnorderedAccess;

            return new D3D11TextureViewPlan(
                shaderResource: sampled || mips,
                renderTarget: renderTarget,
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
        /// identical to the incumbent was why they keep working.
        /// </para>
        /// <para>
        /// A read-only structured buffer takes the shader resource view alone, and a read-write one takes BOTH,
        /// because a read-write storage block is still readable.
        /// </para>
        /// <para>
        /// DECISION U3'S CREATION INVARIANT IS THE OTHER THING DECIDED HERE, and it is the one BACKEND-DIVERGENT
        /// failure this backend has. A <see cref="GpuBufferUsage.UniformBuffer"/> buffer is ring-backed, meaning
        /// one native buffer holding a segment per frame in flight, and the segment is added at bind time by the
        /// constant-buffer bind alone. Every OTHER way of binding a buffer (a vertex or index bind, an indirect
        /// argument read, a structured buffer's full-range RAW view) addresses byte zero, so it would silently
        /// read segment zero while the same buffer's uniform bind read segment N. So the combination is refused at
        /// CREATION rather than discovered as a wrong frame. See <see cref="RejectRingCombination"/>.
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

            bool uniform = !staging && (usage & GpuBufferUsage.UniformBuffer) != 0;
            if (uniform && (usage & ~(GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic)) != 0)
                RejectRingCombination(usage);

            bool structuredRead = (usage & GpuBufferUsage.StructuredBufferReadOnly) != 0;
            bool structuredWrite = (usage & GpuBufferUsage.StructuredBufferReadWrite) != 0;

            D3D11BindUsage bind = D3D11BindUsage.None;
            if (!staging)
            {
                if ((usage & GpuBufferUsage.VertexBuffer) != 0) bind |= D3D11BindUsage.VertexBuffer;
                if ((usage & GpuBufferUsage.IndexBuffer) != 0) bind |= D3D11BindUsage.IndexBuffer;
                if (uniform) bind |= D3D11BindUsage.ConstantBuffer;
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
                indirect: !staging && (usage & GpuBufferUsage.IndirectBuffer) != 0,
                ring: uniform);
        }

        /// <summary>
        /// THE BACKEND-DIVERGENT CREATION FAILURE, in one place with its whole reason attached. A uniform buffer
        /// combined with any other bindable usage is legal on the seam and was accepted by the Veldrid backend, and
        /// it throws here.
        /// <para>
        /// WHY IT CANNOT SIMPLY BE HONOURED. The ring is what makes a per-frame uniform write cost nothing, and it
        /// works by putting <c>FramesInFlight</c> copies of the buffer end to end in one allocation and adding the
        /// frame's base at the constant-buffer bind. No other bind on the seam carries a frame base: a vertex bind
        /// takes the offset the caller passed, an index bind takes none, a structured buffer's RAW view is created
        /// once over the whole allocation, and an indirect argument read names a byte offset. Each of those would
        /// address segment zero while the uniform bind addressed segment N, which is not an error anywhere. It is
        /// one frame's data being read as another's, intermittently, with no diagnostic.
        /// </para>
        /// <para>
        /// WHY IT IS A THROW RATHER THAN A FALLBACK to a non-ring uniform buffer. Falling back would make the
        /// stalling <c>UpdateSubresource</c> path reachable again for exactly the buffers a consumer thought were
        /// the fast ones, silently, and the whole point of the backend is that the per-frame path has no such
        /// branch left in it.
        /// </para>
        /// <para>
        /// It is VACUOUS in the engine today, verified across every renderer call site, which is what makes the
        /// throw safe to add. It is still a divergence, so it is documented as one in the package README rather
        /// than left for a consumer to discover.
        /// </para>
        /// </summary>
        static void RejectRingCombination(GpuBufferUsage usage)
            => throw new ArgumentException(
                $"A buffer was created as {usage} on the native Direct3D 11 backend, which combines "
                + "GpuBufferUsage.UniformBuffer with another way of binding the same bytes. A uniform buffer here "
                + "is RING-BACKED: it holds one segment per frame in flight and the frame's base offset is added "
                + "at the constant-buffer bind. No other bind carries that base, so the vertex, index, indirect or "
                + "structured read would address the first segment while the uniform read addressed the current "
                + "one, and one frame's data would be read as another's with nothing thrown and nothing logged. "
                + "This combination IS accepted by GpuBackendKind.Direct3D11, so it is a documented divergence of "
                + "this backend. Create two buffers, one uniform and one for the other usage.",
                nameof(usage));
    }
}
