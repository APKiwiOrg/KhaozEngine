namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Device-free compile operations, safe to retain on bounded CPU workers.</summary>
internal interface ID3D11ShaderBuildApi
{
    D3D11CompiledPair Pair(string vertex, string pixel, uint flags, D3D11DxbcCache? cache);
    D3D11CompiledCompute Compute(string source, uint flags, D3D11DxbcCache? cache);
}

/// <summary>Native compile/create operations. Production workers retain only the separate CPU compiler.</summary>
internal interface ID3D11ShaderCompilerApi : ID3D11ShaderBuildApi
{
    ID3D11ShaderBuildApi Compilation => this;
    object CreateShader(byte[] dxbc, D3D11ShaderStage stage);
}
