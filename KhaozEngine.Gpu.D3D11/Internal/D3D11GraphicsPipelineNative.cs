using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>The state objects owned by one realized graphics pipeline. Shaders remain caller-owned.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11GraphicsPipelineNative : IDisposable
    {
        internal sealed record Shaders(ID3D11VertexShader Vertex, ID3D11PixelShader Pixel,
            ReadOnlyMemory<byte> VertexBytecode);

        internal Shaders Program { get; }
        internal ID3D11BlendState BlendState { get; }
        internal ID3D11DepthStencilState DepthStencilState { get; }
        internal ID3D11RasterizerState RasterizerState { get; }
        internal ID3D11InputLayout? InputLayout { get; }

        internal D3D11GraphicsPipelineNative(ID3D11Device device, DeviceLiveness liveness,
            D3D11CreationGate creation, in GpuPipelineDescription description,
            D3D11InputElement[] elements, Shaders shaders)
        {
            Program = shaders;
            // Reflection and shader joins never run under the native creation gate.
            RequireContiguousSignatureWindows(shaders.VertexBytecode.Span);
            using var scope = creation.Enter();
            ObjectDisposedException.ThrowIf(liveness.IsDead, device);
            try
            {
                BlendState = CreateBlendStateWindows(device, description);
                DepthStencilState = CreateDepthStencilStateWindows(device, description.DepthStencil);
                RasterizerState = CreateRasterizerStateWindows(device, description.Rasterizer,
                    description.Outputs.SampleCount > 1);
                InputLayout = elements.Length == 0 ? null
                    : CreateInputLayoutWindows(device, elements, shaders.VertexBytecode.Span);
            }
            catch
            {
                if (liveness.IsAlive) Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            InputLayout?.Dispose();
            RasterizerState?.Dispose();
            DepthStencilState?.Dispose();
            BlendState?.Dispose();
        }

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
