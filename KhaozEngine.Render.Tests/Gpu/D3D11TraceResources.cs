using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A BUFFER THE DEVICE-FREE TRACE EMITTER ACCEPTS: it answers <see cref="ID3D11BindableViews"/> the way
    /// <see cref="D3D11Buffer"/> does, with a native buffer and a view for every register file a buffer can reach.
    /// The trace resolves every handle through the same seam the real emitter does
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1691), so a <see cref="FakeBuffer"/> is a buffer from
    /// another backend there and is refused. The views are plain objects, which is all the resolution asks of one.
    /// </summary>
    internal sealed class TraceBuffer : IGpuBuffer, ID3D11BindableViews
    {
        internal TraceBuffer(uint sizeInBytes) => SizeInBytes = sizeInBytes;

        public uint SizeInBytes { get; }

        object? ID3D11BindableViews.BufferObject { get; } = new object();
        object? ID3D11BindableViews.ShaderResourceViewObject { get; } = new object();
        object? ID3D11BindableViews.UnorderedAccessViewObject { get; } = new object();
        object? ID3D11BindableViews.SamplerStateObject => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A texture the trace emitter accepts in a bind: shader resource and unordered access views, and no
    /// buffer or sampler state, as <see cref="D3D11Texture"/> answers. The texture transfers keep their own
    /// refusal in the real emitter, so this matters for binds only.</summary>
    internal sealed class TraceTexture : IGpuTexture, ID3D11BindableViews
    {
        internal TraceTexture(uint width = 4, uint height = 4, GpuPixelFormat format = GpuPixelFormat.R8G8B8A8UNorm)
        {
            Width = width;
            Height = height;
            Format = format;
        }

        public uint Width { get; }
        public uint Height { get; }
        public uint MipLevels => 1;
        public uint SampleCount => 1;
        public GpuPixelFormat Format { get; }

        object? ID3D11BindableViews.BufferObject => null;
        object? ID3D11BindableViews.ShaderResourceViewObject { get; } = new object();
        object? ID3D11BindableViews.UnorderedAccessViewObject { get; } = new object();
        object? ID3D11BindableViews.SamplerStateObject => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A sampler the trace emitter accepts: a sampler state and nothing else, as
    /// <see cref="D3D11Sampler"/> answers.</summary>
    internal sealed class TraceSampler : IGpuSampler, ID3D11BindableViews
    {
        object? ID3D11BindableViews.BufferObject => null;
        object? ID3D11BindableViews.ShaderResourceViewObject => null;
        object? ID3D11BindableViews.UnorderedAccessViewObject => null;
        object? ID3D11BindableViews.SamplerStateObject { get; } = new object();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A FRAMEBUFFER THE TRACE EMITTER ACCEPTS: it answers <see cref="ID3D11RenderTargetSurface"/> with one view
    /// per declared colour attachment and a depth view exactly when it declares a depth attachment, which is how
    /// both of this backend's framebuffer types build theirs. <see cref="D3D11SwapchainFramebuffer"/> is device-free
    /// too, but it always has one colour attachment, and a pass test needs other shapes.
    /// </summary>
    internal sealed class TraceFramebuffer : IGpuFramebuffer, ID3D11RenderTargetSurface
    {
        readonly object[] _targets;

        internal TraceFramebuffer(GpuOutputDescription outputs, uint width, uint height)
        {
            Outputs = outputs;
            Width = width;
            Height = height;
            _targets = new object[outputs.Colour?.Length ?? 0];
            for (int i = 0; i < _targets.Length; i++) _targets[i] = new object();
            DepthStencil = outputs.Depth is null ? null : new object();
        }

        public GpuOutputDescription Outputs { get; }
        public uint Width { get; }
        public uint Height { get; }

        public int RenderTargetCount => _targets.Length;
        public object RenderTargetAt(int index) => _targets[index];
        public object? DepthStencil { get; }

        public void Dispose()
        {
        }
    }
}
