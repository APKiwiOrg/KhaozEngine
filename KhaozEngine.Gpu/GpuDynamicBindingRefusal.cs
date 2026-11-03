namespace KhaozEngine.Gpu;

/// <summary>The rules of the GpuDynamicBinding refusal family.</summary>
public enum GpuDynamicBindingRefusal
{
    /// <summary>Refuses a nonzero dynamic offset on a set with no dynamic layout element.</summary>
    DynamicElementMissing,
}
