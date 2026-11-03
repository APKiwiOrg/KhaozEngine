namespace KhaozEngine.Gpu;

/// <summary>The rules that refuse buffer usage combinations.</summary>
public enum GpuBufferUsageRefusal
{
    /// <summary>Refuses a ring-backed uniform buffer combined with another binding of the same bytes.</summary>
    UniformBindingCombination,
}
