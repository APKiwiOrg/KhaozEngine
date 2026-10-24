using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal;

[SupportedOSPlatform("windows")]
internal sealed class D3D11ShaderCompilerApi(ID3D11Device device, KhaozEngine.Gpu.Internal.SpirvCompileCache? memo = null) : ID3D11ShaderCompilerApi
{
    public D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache)
        => D3D11ShaderBuild.Pair(vertex, pixel, flags, cache, memo: memo);
    public D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache)
        => D3D11ShaderBuild.Compute(source, flags, cache, memo: memo);
    public object CreateShader(byte[] dxbc, D3D11ShaderStage stage) => stage switch
    {
        D3D11ShaderStage.Vertex => device.CreateVertexShader(dxbc),
        D3D11ShaderStage.Fragment => device.CreatePixelShader(dxbc),
        D3D11ShaderStage.Compute => device.CreateComputeShader(dxbc),
        _ => throw new System.ArgumentOutOfRangeException(nameof(stage))
    };
}
