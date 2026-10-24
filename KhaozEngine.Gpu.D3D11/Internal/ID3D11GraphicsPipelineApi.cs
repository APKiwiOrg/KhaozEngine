using System;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal interface ID3D11GraphicsPipelineApi
{
    D3D11GraphicsPipelineObjects Create(in GpuPipelineDescription description, ID3D11ShaderSet shaders,
        D3D11InputElement[] elements);
}

/// <summary>One eager native state set, released only by its pipeline. No interop value-type fields.</summary>
internal sealed record D3D11GraphicsPipelineObjects(object Blend, object Depth, object Rasterizer,
    object? InputLayout, uint Topology, object VertexShader, object PixelShader) : IDisposable
{
    public void Dispose()
    {
        ((IDisposable)Blend).Dispose();
        ((IDisposable)Depth).Dispose();
        ((IDisposable)Rasterizer).Dispose();
        (InputLayout as IDisposable)?.Dispose();
    }
}
