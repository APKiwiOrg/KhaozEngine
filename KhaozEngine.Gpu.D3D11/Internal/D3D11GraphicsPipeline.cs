using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>A managed pipeline plan. Native state and input layouts are realized at first bind.
    /// Construction snapshots caller data without joining independent shader compiles.</summary>
    internal sealed class D3D11GraphicsPipeline : IGpuPipeline, ID3D11PipelineState, ID3D11PipelineLayouts, IGpuPipelineDiagnostics, IGpuPipelineConstructionDiagnostics, IGpuBudgetedPipelineDiagnostics
    {
        readonly DeviceLiveness _liveness;
        readonly ID3D11ShaderSet _shaderSet;
        readonly D3D11DeferredResource<ReadOnlyMemory<byte>, D3D11GraphicsPipelineObjects> _resource;
        readonly D3D11ShaderCompilation<D3D11PipelineDiagnostics> _diagnostics;

        [SupportedOSPlatform("windows")]
        internal D3D11GraphicsPipeline(ID3D11Device device, DeviceLiveness liveness,
            in GpuPipelineDescription description) : this(device, liveness, D3D11CreationGate.For(null), description) { }

        [SupportedOSPlatform("windows")]
        internal D3D11GraphicsPipeline(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation,
            in GpuPipelineDescription description) : this(liveness, description, new NativeApi(device ?? throw new ArgumentNullException(nameof(device)), liveness, creation)) { }

        internal D3D11GraphicsPipeline(DeviceLiveness liveness, in GpuPipelineDescription description,
            ID3D11GraphicsPipelineApi api)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            _liveness = liveness;

            if (description.ShaderSet is not ID3D11ShaderSet shaders)
            {
                throw new ArgumentException(
                    "A graphics pipeline for the native Direct3D 11 backend needs a shader set this backend "
                    + "compiled. A set from another backend holds another backend's compiled modules.",
                    nameof(description));
            }

            _shaderSet = shaders;
            TopologyKind = description.Topology;
            BlendFactor = description.BlendFactor;
            ResourceLayouts = D3D11ResourceLayout.RequireAll(description.ResourceLayouts, "graphics");

            InputElements = D3D11InputLayoutPlan.Build(description.VertexLayouts, out uint[] strides);
            VertexStrides = strides;

            // Diagnostics and deferred creation must see the same immutable construction inputs.
            var snapshot = description;
            snapshot.BlendAttachments = (GpuBlendAttachment[])(description.BlendAttachments ?? []).Clone();
            snapshot.Outputs = new GpuOutputDescription(description.Outputs.Depth,
                (GpuPixelFormat[])(description.Outputs.Colour ?? []).Clone()).WithSampleCount(description.Outputs.SampleCount);
            snapshot.VertexLayouts = new(description.VertexLayouts?.Count ?? 0);
            foreach (var layout in description.VertexLayouts ?? [])
                snapshot.VertexLayouts.Add(new(layout.Stride, layout.InstanceStepRate,
                    (GpuVertexElement[])(layout.Elements ?? []).Clone()));
            _diagnostics = shaders is D3D11ShaderSet owner
                ? D3D11PipelineIdentity.Graphics(owner.Preparation, ResourceLayouts, snapshot)
                : D3D11ShaderCompilation<D3D11PipelineDiagnostics>.Completed(
                    D3D11PipelineIdentity.Graphics(null, null, ResourceLayouts, snapshot));
            _resource = new(liveness, () => shaders.VertexShaderBytecode,
                _ => api.Create(snapshot, shaders, InputElements));
        }

        /// <summary>The compiled vertex shader.</summary>
        internal ID3D11VertexShader VertexShader => (ID3D11VertexShader)_resource.Value.VertexShader;
        /// <summary>The compiled pixel shader.</summary>
        internal ID3D11PixelShader PixelShader => (ID3D11PixelShader)_resource.Value.PixelShader;
        /// <summary>The blend state object.</summary>
        internal ID3D11BlendState BlendState => (ID3D11BlendState)_resource.Value.Blend;
        /// <summary>The depth-stencil state object.</summary>
        internal ID3D11DepthStencilState DepthStencilState => (ID3D11DepthStencilState)_resource.Value.Depth;
        /// <summary>The rasterizer state object.</summary>
        internal ID3D11RasterizerState RasterizerState => (ID3D11RasterizerState)_resource.Value.Rasterizer;
        /// <summary>The input layout, or null for a pass with no vertex buffers (the fullscreen ones).</summary>
        internal ID3D11InputLayout? InputLayout => (ID3D11InputLayout?)_resource.Value.InputLayout;
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
        [SupportedOSPlatform("windows")]
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
        // Seven upcasts of stored fields and nothing else. Each one hands back the SAME instance on every read,
        // which is the whole contract: the cache asks whether what it holds is what is already bound, so a value
        // built per access would never compare equal, every bind would report a change and the cache would be
        // defeated without anything throwing or logging. Every Direct3D handle here is a reference type, so the
        // upcast to object boxes nothing, and the topology is the uint the interface asks for rather than a
        // boxed enum.

        /// <inheritdoc/>
        object? ID3D11PipelineState.VertexShader => _resource.Value.VertexShader;

        /// <inheritdoc/>
        object? ID3D11PipelineState.PixelShader => _resource.Value.PixelShader;

        /// <inheritdoc/>
        object? ID3D11PipelineState.BlendState => _resource.Value.Blend;

        /// <inheritdoc/>
        System.Numerics.Vector4 ID3D11PipelineState.BlendFactor => BlendFactor;

        /// <inheritdoc/>
        object? ID3D11PipelineState.DepthStencilState => _resource.Value.Depth;

        /// <summary>
        /// ALWAYS ZERO, and stated here rather than left implicit. The GPU seam carries no stencil state at all,
        /// so <see cref="CreateDepthStencilStateWindows"/> builds every object with <c>StencilEnable = false</c>
        /// and there is no engine value a reference could come from. It is part of the depth-stencil cache key
        /// anyway (issue #454), so the day the seam grows a stencil pass the key is already right.
        /// </summary>
        uint ID3D11PipelineState.StencilReference => 0u;

        /// <inheritdoc/>
        object? ID3D11PipelineState.RasterizerState => _resource.Value.Rasterizer;

        /// <inheritdoc/>
        object? ID3D11PipelineState.InputLayout => _resource.Value.InputLayout;

        /// <inheritdoc/>
        uint ID3D11PipelineState.PrimitiveTopology => _resource.Value.Topology;

        /// <inheritdoc/>
        uint[] ID3D11PipelineState.VertexStrides => VertexStrides;

        /// <summary>The same array as <see cref="ResourceLayouts"/>, reached through the seam the bind flush asks
        /// through. Explicit because the typed property is internal and an interface implementation cannot be, and
        /// separate from <see cref="ID3D11PipelineState"/> because a compute pipeline answers this one and none of
        /// the seven.</summary>
        D3D11ResourceLayout[] ID3D11PipelineLayouts.ResourceLayouts => ResourceLayouts;

        public GpuPipelineConstructionIdentity ConstructionIdentity => _diagnostics.Join().ConstructionIdentity;
        public GpuPipelineCompilerReadResult ReadCompilerReport(GpuPipelineCompilerReadRequest request,
            System.Action<string> checkOperation, System.Threading.CancellationToken cancellation)
            => _diagnostics.Join().Read(IsDisposed || _liveness.IsDead
                || _shaderSet is D3D11ShaderSet s && (s.IsDisposed || s.DeviceIsDead), request, checkOperation, cancellation);

        public GpuPipelineCompilerReport ReadCompilerReport()
            => _diagnostics.Join().Read(IsDisposed || _liveness.IsDead
                || _shaderSet is D3D11ShaderSet s && (s.IsDisposed || s.DeviceIsDead));

        public void Dispose() => _resource.Dispose();

        [SupportedOSPlatform("windows")]
        sealed class NativeApi(ID3D11Device device, DeviceLiveness liveness, D3D11CreationGate creation) : ID3D11GraphicsPipelineApi
        {
            public D3D11GraphicsPipelineObjects Create(in GpuPipelineDescription d, ID3D11ShaderSet shaders,
                D3D11InputElement[] elements)
            {
                ArgumentNullException.ThrowIfNull(device);
                RequireContiguousSignatureWindows(shaders.VertexShaderBytecode.Span);
                var vertex = shaders.VertexShader;
                var pixel = shaders.PixelShader;
                using var scope = creation.Enter();
                ObjectDisposedException.ThrowIf(liveness.IsDead, device);
                uint topology = TopologyValueWindows(d.Topology);
                ID3D11BlendState? blend = null;
                ID3D11DepthStencilState? depth = null;
                ID3D11RasterizerState? rasterizer = null;
                ID3D11InputLayout? input = null;
                try
                {
                    blend = CreateBlendStateWindows(device, d);
                    depth = CreateDepthStencilStateWindows(device, d.DepthStencil);
                    rasterizer = CreateRasterizerStateWindows(device, d.Rasterizer, d.Outputs.SampleCount > 1);
                    input = elements.Length == 0 ? null : CreateInputLayoutWindows(device, elements, shaders.VertexShaderBytecode.Span);
                    return new(blend, depth, rasterizer, input, topology, vertex, pixel);
                }
                catch
                {
                    if (liveness.IsAlive)
                    { input?.Dispose(); rasterizer?.Dispose(); depth?.Dispose(); blend?.Dispose(); }
                    throw;
                }
            }
        }

        // The one Vortice-touching body this seam added, and it is behind the package's usual boundary: NoInlining
        // with no interop type in the signature, so nothing resolves the assembly until this actually runs, which
        // is inside a constructor that already needs a live device.
        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint TopologyValueWindows(GpuPrimitiveTopology topology) => (uint)D3D11Formats.ToTopology(topology);

        // A vertex-input-free pipeline (the fullscreen passes) has nothing to reflect and nothing to hole, so the
        // empty case passes rather than throwing: the seam builds those pipelines with no vertex layouts at all.
        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RequireContiguousSignatureWindows(ReadOnlySpan<byte> vertexBytecode)
        {
            if (vertexBytecode.IsEmpty) return;
            const string label = "graphics pipeline vertex shader";
            D3D11ShaderSignature.RequireContiguousUserSemantics(
                D3D11Fxc.ReflectVertexInputs(vertexBytecode, label), label);
        }

        // IndependentBlendEnable is on, so each attachment keeps its own state. The engine's multiple-render-target
        // model pass relies on it: one attachment blends while another is set to preserve its destination.
        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11BlendState CreateBlendStateWindows(ID3D11Device device, in GpuPipelineDescription description)
        {
            GpuBlendAttachment[] attachments = description.BlendAttachments ?? Array.Empty<GpuBlendAttachment>();
            var d = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = true };

            for (int i = 0; i < attachments.Length && i < d.RenderTarget.Length; i++)
            {
                GpuBlendAttachment a = attachments[i];
                d.RenderTarget[i].IsBlendEnabled = a.BlendEnabled;
                d.RenderTarget[i].RenderTargetWriteMask = ColorWriteEnable.All;
                d.RenderTarget[i].SourceBlend = D3D11Formats.ToBlend(a.SourceColorFactor);
                d.RenderTarget[i].DestinationBlend = D3D11Formats.ToBlend(a.DestinationColorFactor);
                d.RenderTarget[i].BlendOperation = D3D11Formats.ToBlendOperation(a.ColorFunction);
                d.RenderTarget[i].SourceBlendAlpha = D3D11Formats.ToBlend(a.SourceAlphaFactor);
                d.RenderTarget[i].DestinationBlendAlpha = D3D11Formats.ToBlend(a.DestinationAlphaFactor);
                d.RenderTarget[i].BlendOperationAlpha = D3D11Formats.ToBlendOperation(a.AlphaFunction);
            }

            return device.CreateBlendState(d);
        }

        // No stencil anywhere in the seam, so the stencil half stays at its defaults rather than being mapped from
        // engine values that do not exist.
        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11DepthStencilState CreateDepthStencilStateWindows(ID3D11Device device,
            in GpuDepthStencilState state)
        {
            var d = new DepthStencilDescription
            {
                DepthEnable = state.DepthTestEnabled,
                DepthWriteMask = state.DepthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
                DepthFunc = D3D11Formats.ToComparison(state.Comparison),
                StencilEnable = false,
            };
            return device.CreateDepthStencilState(d);
        }

        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11RasterizerState CreateRasterizerStateWindows(ID3D11Device device,
            in GpuRasterizerState state, bool multisampled)
        {
            var d = new RasterizerDescription
            {
                CullMode = D3D11Formats.ToCullMode(state.CullMode),
                FillMode = D3D11Formats.ToFillMode(state.FillMode),
                DepthClipEnable = state.DepthClipEnabled,
                ScissorEnable = state.ScissorTestEnabled,
                FrontCounterClockwise = state.FrontFace == GpuFrontFace.CounterClockwise,
                MultisampleEnable = multisampled,
            };
            return device.CreateRasterizerState(d);
        }

        [SupportedOSPlatform("windows")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11InputLayout CreateInputLayoutWindows(ID3D11Device device, D3D11InputElement[] elements,
            ReadOnlySpan<byte> vertexBytecode)
        {
            if (vertexBytecode.IsEmpty)
            {
                throw new ArgumentException(
                    "An input layout is validated against the vertex shader's compiled signature, so the vertex "
                    + "bytecode cannot be empty.", nameof(vertexBytecode));
            }

            var native = new InputElementDescription[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                D3D11InputElement e = elements[i];
                native[i] = new InputElementDescription(
                    e.SemanticName,
                    (int)e.SemanticIndex,
                    D3D11Formats.ToDxgiFormat(e.Format),
                    (int)e.OffsetBytes,
                    (int)e.Slot,
                    e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                    (int)e.InstanceStepRate);
            }

            return device.CreateInputLayout(native, vertexBytecode.ToArray());
        }
    }
}
