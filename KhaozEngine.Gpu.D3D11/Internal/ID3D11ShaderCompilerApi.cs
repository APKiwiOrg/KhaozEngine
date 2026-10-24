namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Native compile/create operations only. The compiler retains and binds the actual returned artifacts.</summary>
internal interface ID3D11ShaderCompilerApi
{
    D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache);
    D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache);
    object CreateShader(byte[] dxbc, D3D11ShaderStage stage);
}
