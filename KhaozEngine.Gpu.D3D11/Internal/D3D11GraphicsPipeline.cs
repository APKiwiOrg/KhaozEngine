using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
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
    /// THERE IS NO STATE CACHE, and its absence is decision X2 rather than an omission. The incumbent carried a
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
    /// <para>
    /// IT ALSO ANSWERS <see cref="ID3D11PipelineState"/>, which is how the redundancy caches of decision R6 read
    /// it: get-only members over the state above, EXPLICITLY implemented because most of them collide by
    /// name with the typed properties the emitter uses to make the calls. That is the split the interface
    /// describes, with this seam answering what changed and the typed members answering with what, and it costs
    /// no downcast per bind either way. Every member returns a stored field, because the caches compare by
    /// reference identity and a value built per access is never equal to the last one. Two of them are the
    /// ARGUMENTS that ride a state object (the blend factor and the stencil reference, issue #454) rather than
    /// objects, so those are compared by value.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11GraphicsPipeline : IGpuPipeline, ID3D11PipelineState, ID3D11PipelineLayouts
    {
        readonly DeviceLiveness _liveness;

        // The topology's D3D_PRIMITIVE_TOPOLOGY value, resolved once here rather than read from Topology per
        // access. A uint field, not a Vortice enum one: a Vortice VALUE-TYPE field anywhere in this package makes
        // the CLR resolve the interop assembly the moment the declaring type is loaded, and the load-path guard
        // asserts process-wide that nothing pulls it in off Windows. The cache's uint is what the interface asks
        // for anyway, so the conversion happens once per pipeline instead of once per bind.
        readonly uint _primitiveTopology;

        internal D3D11GraphicsPipeline(ID3D11Device device, DeviceLiveness liveness,
            in GpuPipelineDescription description)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;

            if (description.ShaderSet is not ID3D11ShaderSet shaders)
            {
                // Not reachable device-free: the null check above runs first and this type is Windows-only, so
                // this guard lands on the Windows leg with device creation rather than in the headless suite.
                throw new ArgumentException(
                    "A graphics pipeline for the native Direct3D 11 backend needs a shader set this backend "
                    + "compiled. A set from another backend holds another backend's compiled modules.",
                    nameof(description));
            }

            VertexShader = shaders.VertexShader;
            PixelShader = shaders.PixelShader;
            TopologyKind = description.Topology;
            _primitiveTopology = TopologyValueWindows(description.Topology);
            BlendFactor = description.BlendFactor;
            ResourceLayouts = D3D11ResourceLayout.RequireAll(description.ResourceLayouts, "graphics");

            InputElements = D3D11InputLayoutPlan.Build(description.VertexLayouts, out uint[] strides);
            VertexStrides = strides;

            // DECISION S5, THE SECOND SITE. The shader path checks every module it compiles, and this checks the
            // bytes an input layout is about to be validated against, whatever produced them: a set from the disk
            // cache never passed through a compiler in this process, and a set built by hand never passed through
            // the shader path at all. It costs one reflection per pipeline on the creation path, and it buys a
            // failure that names the shader instead of a frame that renders with no colour in it.
            RequireContiguousSignatureWindows(shaders.VertexShaderBytecode.Span);

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

        // ---- ID3D11PipelineState: what the redundancy caches read (decision R6) ----
        //
        // Seven upcasts of stored fields and nothing else. Each one hands back the SAME instance on every read,
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
        /// so <see cref="CreateDepthStencilStateWindows"/> builds every object with <c>StencilEnable = false</c>
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

        // The one Vortice-touching body this seam added, and it is behind the package's usual boundary: NoInlining
        // with no interop type in the signature, so nothing resolves the assembly until this actually runs, which
        // is inside a constructor that already needs a live device.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static uint TopologyValueWindows(GpuPrimitiveTopology topology) => (uint)D3D11Formats.ToTopology(topology);

        // A vertex-input-free pipeline (the fullscreen passes) has nothing to reflect and nothing to hole, so the
        // empty case passes rather than throwing: the seam builds those pipelines with no vertex layouts at all.
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
