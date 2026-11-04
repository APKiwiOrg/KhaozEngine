namespace KhaozEngine.Gpu.Metal;

/// <summary>Why MetalShaderFunction refused an operation.</summary>
public enum MetalShaderFunctionRefusal
{
    /// <summary>Refuses reading a released Metal shader function.</summary>
    FunctionReleased,

    /// <summary>Refuses a shader-set function for a stage the set does not carry.</summary>
    StageMissing,
}
