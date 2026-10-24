namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>CPU shader artifacts and their immutable diagnostic evidence, prepared before native creation.</summary>
internal readonly record struct D3D11PreparedGraphics(D3D11CompiledPair Code,
    D3D11ShaderEvidence? Vertex, D3D11ShaderEvidence? Pixel);
internal readonly record struct D3D11PreparedCompute(D3D11CompiledCompute Code, D3D11ShaderEvidence? Evidence);
