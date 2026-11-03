namespace KhaozEngine.Gpu.Metal;

/// <summary>Why MetalBufferPolicy refused an operation.</summary>
public enum MetalBufferPolicyRefusal
{
    /// <summary>Refuses uniform and structured usage on the same ring-backed buffer.</summary>
    UniformStructuredCombination,
    /// <summary>Refuses a CPU buffer write exceeding the logical buffer.</summary>
    WriteOutsideBuffer,
}
