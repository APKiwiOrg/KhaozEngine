using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuPipeline"/> for the native Direct3D 11 backend. Every state object a draw needs is built
    /// HERE, at pipeline creation, and stored: the blend state, the depth-stencil state, the rasterizer state and
    /// the input layout, plus the topology, the per-slot vertex strides, the two shaders and the resource layouts
    /// the register scheme flattens across.
    /// <para>
    /// THE INPUT LAYOUT IS WHY THIS IS THE RIGHT MOMENT. Direct3D 11 validates an input layout against a real
    /// compiled vertex shader signature, and pipeline creation is the one point where the vertex layouts and the
    /// vertex bytecode are both in hand.
    /// </para>
    /// <para>
    /// THERE IS NO STATE CACHE, and its absence is decision X2 rather than an omission. The incumbent carries a
    /// 328-line resource cache keyed on each state description. The Direct3D 11 runtime already returns an
    /// existing object for an identical state description, so the cache duplicates a service the runtime provides,
    /// and it is a lock plus four dictionaries on the creation path to do it. That is a claimed runtime behaviour
    /// rather than one this design measured. If the claim is wrong, the cost is a few more state objects than
    /// necessary at load time, which is an allocation count and not a correctness failure, and the measurement
    /// that would settle it is a creation-time allocation count on WARP.
    /// </para>
    /// <para>
    /// Everything here is created eagerly and disposed under the liveness gate. A pipeline creates four objects
    /// and a draw creates none.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11GraphicsPipeline : IGpuPipeline
    {
        readonly D3D11DeviceLiveness _liveness;

        internal D3D11GraphicsPipeline(ID3D11Device device, D3D11DeviceLiveness liveness,
            in GpuPipelineDescription description)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;

            if (description.ShaderSet is not ID3D11ShaderSet shaders)
            {
                throw new ArgumentException(
                    "A graphics pipeline for the native Direct3D 11 backend needs a shader set this backend "
                    + "compiled. A set from another backend holds another backend's compiled modules.",
                    nameof(description));
            }

            VertexShader = shaders.VertexShader;
            PixelShader = shaders.PixelShader;
            TopologyKind = description.Topology;
            BlendFactor = description.BlendFactor;
            ResourceLayouts = ToLayouts(description.ResourceLayouts);

            InputElements = D3D11InputLayoutPlan.Build(description.VertexLayouts, out uint[] strides);
            VertexStrides = strides;

            bool multisampled = description.Outputs.SampleCount > 1;
            BlendState = CreateBlendStateWindows(device, description);
            DepthStencilState = CreateDepthStencilStateWindows(device, description.DepthStencil);
            RasterizerState = CreateRasterizerStateWindows(device, description.Rasterizer, multisampled);
            InputLayout = InputElements.Length == 0
                ? null
                : CreateInputLayoutWindows(device, InputElements, shaders.VertexShaderBytecode.Span);
        }

        /// <summary>The compiled vertex shader.</summary>
        internal ID3D11VertexShader VertexShader { get; }
        /// <summary>The compiled pixel shader.</summary>
        internal ID3D11PixelShader PixelShader { get; }
        /// <summary>The blend state object.</summary>
        internal ID3D11BlendState BlendState { get; }
        /// <summary>The depth-stencil state object.</summary>
        internal ID3D11DepthStencilState DepthStencilState { get; }
        /// <summary>The rasterizer state object.</summary>
        internal ID3D11RasterizerState RasterizerState { get; }
        /// <summary>The input layout, or null for a pass with no vertex buffers (the fullscreen ones).</summary>
        internal ID3D11InputLayout? InputLayout { get; }
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
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

            BlendState.Dispose();
            DepthStencilState.Dispose();
            RasterizerState.Dispose();
            InputLayout?.Dispose();
        }

        static D3D11ResourceLayout[] ToLayouts(IGpuResourceLayout[]? layouts)
        {
            if (layouts is null || layouts.Length == 0) return Array.Empty<D3D11ResourceLayout>();

            var result = new D3D11ResourceLayout[layouts.Length];
            for (int i = 0; i < layouts.Length; i++)
            {
                result[i] = layouts[i] as D3D11ResourceLayout
                    ?? throw new ArgumentException(
                        $"Resource layout {i} was not created by the native Direct3D 11 backend, so it carries no "
                        + "register numbering this pipeline can flatten.", nameof(layouts));
            }
            return result;
        }

        // IndependentBlendEnable is on, so each attachment keeps its own state. The engine's multiple-render-target
        // model pass relies on it: one attachment blends while another is set to preserve its destination.
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
