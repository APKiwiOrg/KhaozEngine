using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>A managed pipeline plan. Native shaders, state and input layout are realized at first bind.
    /// Creation never joins CPU compiles, so constructing a batch can queue every independent program.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11GraphicsPipeline : IGpuPipeline, ID3D11PipelineState, ID3D11PipelineLayouts
    {
        readonly D3D11DeferredResource<ReadOnlyMemory<byte>, D3D11GraphicsPipelineNative> _resource;

        // The topology's D3D_PRIMITIVE_TOPOLOGY value, resolved once here rather than read from Topology per
        // access. A uint field, not a Vortice enum one: a Vortice VALUE-TYPE field anywhere in this package makes
        // the CLR resolve the interop assembly the moment the declaring type is loaded, and the load-path guard
        // asserts process-wide that nothing pulls it in off Windows. The cache's uint is what the interface asks
        // for anyway, so the conversion happens once per pipeline instead of once per bind.
        readonly uint _primitiveTopology;

        internal D3D11GraphicsPipeline(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation,
            in GpuPipelineDescription description)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            if (description.ShaderSet is not ID3D11ShaderSet shaders)
            {
                // Not reachable device-free: the null check above runs first and this type is Windows-only, so
                // this guard lands on the Windows leg with device creation rather than in the headless suite.
                throw new ArgumentException(
                    "A graphics pipeline for the native Direct3D 11 backend needs a shader set this backend "
                    + "compiled. A set from another backend holds another backend's compiled modules.",
                    nameof(description));
            }

            TopologyKind = description.Topology;
            _primitiveTopology = TopologyValueWindows(description.Topology);
            BlendFactor = description.BlendFactor;
            ResourceLayouts = D3D11ResourceLayout.RequireAll(description.ResourceLayouts, "graphics");

            InputElements = D3D11InputLayoutPlan.Build(description.VertexLayouts, out uint[] strides);
            VertexStrides = strides;

            // Snapshot mutable caller data now, just as eager native creation did. The element plan and
            // resource layouts above already own their arrays. Only these blend values are read at first bind.
            GpuPipelineDescription snapshot = description;
            snapshot.BlendAttachments = description.BlendAttachments is null ? []
                : (GpuBlendAttachment[])description.BlendAttachments.Clone();
            _resource = new(liveness,
                () => shaders.VertexShaderBytecode,
                bytecode => new(device, liveness, creation, snapshot, InputElements,
                    new(shaders.VertexShader, shaders.PixelShader, bytecode)));
        }

        /// <summary>The compiled vertex shader.</summary>
        internal ID3D11VertexShader VertexShader => _resource.Value.Program.Vertex;
        /// <summary>The compiled pixel shader.</summary>
        internal ID3D11PixelShader PixelShader => _resource.Value.Program.Pixel;
        /// <summary>The blend state object.</summary>
        internal ID3D11BlendState BlendState => _resource.Value.BlendState;
        /// <summary>The depth-stencil state object.</summary>
        internal ID3D11DepthStencilState DepthStencilState => _resource.Value.DepthStencilState;
        /// <summary>The rasterizer state object.</summary>
        internal ID3D11RasterizerState RasterizerState => _resource.Value.RasterizerState;
        /// <summary>The input layout, or null for a pass with no vertex buffers (the fullscreen ones).</summary>
        internal ID3D11InputLayout? InputLayout => _resource.Value.InputLayout;
        /// <summary>The engine-side input element plan the layout was built from, kept for diagnostics.</summary>
        internal D3D11InputElement[] InputElements { get; }
        /// <summary>Per-slot vertex stride, indexed by vertex buffer slot.</summary>
        internal uint[] VertexStrides { get; }
        /// <summary>The declared primitive topology, kept in engine form.</summary>
        internal GpuPrimitiveTopology TopologyKind { get; }

        /// <summary>
        /// Primitive topology, set on the input assembler at bind. COMPUTED from <see cref="TopologyKind"/> rather
        /// than stored, and that is not a style choice: a Vortice value-type FIELD anywhere in this package makes
        /// the CLR resolve the Vortice assembly the moment the declaring type is loaded, and the load-path guard
        /// asserts process-wide that nothing pulls the interop in off Windows. A reflection scan over the
        /// assembly's types is enough to trip it, and the suite has one. Fields of an interface type are fine,
        /// since a reference field needs no layout resolution. The conversion is a five-way switch reached once
        /// per pipeline bind, and the redundancy caches make that once per pipeline CHANGE.
        /// </summary>
        internal Vortice.Direct3D.PrimitiveTopology Topology => D3D11Formats.ToTopology(TopologyKind);
        /// <summary>The constant blend factor.</summary>
        internal System.Numerics.Vector4 BlendFactor { get; }

        /// <summary>The resource layouts in PIPELINE-ARRAY order, which is the order the register scheme flattens
        /// the sets in. A set bound at slot k indexes this array.</summary>
        internal D3D11ResourceLayout[] ResourceLayouts { get; }

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed => _resource.IsDisposed;

        // ---- ID3D11PipelineState: what the redundancy caches read (decision R6) ----
        //
        // Seven upcasts of the same realized state on every read. Each one hands back the SAME instance on every read,
        // which is the whole contract: the cache asks whether what it holds is what is already bound, so a value
        // built per access would never compare equal, every bind would report a change and the cache would be
        // defeated without anything throwing or logging. Every Direct3D handle here is a reference type, so the
        // upcast to object boxes nothing, and the topology is the uint the interface asks for rather than a
        // boxed enum.

        /// <inheritdoc/>
        object? ID3D11PipelineState.VertexShader => VertexShader;

        /// <inheritdoc/>
        object? ID3D11PipelineState.PixelShader => PixelShader;

        /// <inheritdoc/>
        object? ID3D11PipelineState.BlendState => BlendState;

        /// <inheritdoc/>
        System.Numerics.Vector4 ID3D11PipelineState.BlendFactor => BlendFactor;

        /// <inheritdoc/>
        object? ID3D11PipelineState.DepthStencilState => DepthStencilState;

        /// <summary>
        /// ALWAYS ZERO, and stated here rather than left implicit. The GPU seam carries no stencil state at all,
        /// so <c>D3D11GraphicsPipelineNative</c> builds every object with <c>StencilEnable = false</c>
        /// and there is no engine value a reference could come from. It is part of the depth-stencil cache key
        /// anyway (issue #454), so the day the seam grows a stencil pass the key is already right.
        /// </summary>
        uint ID3D11PipelineState.StencilReference => 0u;

        /// <inheritdoc/>
        object? ID3D11PipelineState.RasterizerState => RasterizerState;

        /// <inheritdoc/>
        object? ID3D11PipelineState.InputLayout => InputLayout;

        /// <inheritdoc/>
        uint ID3D11PipelineState.PrimitiveTopology => _primitiveTopology;

        /// <inheritdoc/>
        uint[] ID3D11PipelineState.VertexStrides => VertexStrides;

        /// <summary>The same array as <see cref="ResourceLayouts"/>, reached through the seam the bind flush asks
        /// through. Explicit because the typed property is internal and an interface implementation cannot be, and
        /// separate from <see cref="ID3D11PipelineState"/> because a compute pipeline answers this one and none of
        /// the seven.</summary>
        D3D11ResourceLayout[] ID3D11PipelineLayouts.ResourceLayouts => ResourceLayouts;

        public void Dispose() => _resource.Dispose();

        // The one Vortice-touching body this seam added, and it is behind the package's usual boundary: NoInlining
        // with no interop type in the signature, so nothing resolves the assembly until this actually runs, which
        // is inside a constructor that already needs a live device.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint TopologyValueWindows(GpuPrimitiveTopology topology) => (uint)D3D11Formats.ToTopology(topology);

    }
}
