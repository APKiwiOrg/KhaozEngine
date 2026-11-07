namespace KhaozEngine.Gpu.D3D11;

/// <summary>Why D3D11ShaderPlatform refused an operation.</summary>
public enum D3D11ShaderPlatformRefusal
{
    /// <summary>Refuses native FXC validation on a platform without d3dcompiler.</summary>
    FxcUnavailableOnPlatform,
}
