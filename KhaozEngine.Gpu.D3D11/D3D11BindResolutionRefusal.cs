namespace KhaozEngine.Gpu.D3D11;

/// <summary>The rules of the D3D11BindResolution refusal family.</summary>
public enum D3D11BindResolutionRefusal
{
    /// <summary>Refuses a null constant buffer carrying a nonzero window.</summary>
    NullBufferWindowNonzero,
    /// <summary>Refuses transposing a constant-buffer span into undersized scratch.</summary>
    TransposeScratchTooSmall,
    /// <summary>Refuses a resource without the native view needed by the register file.</summary>
    ViewMissing,
}
