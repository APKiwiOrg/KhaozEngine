namespace KhaozEngine.Gpu;

/// <summary>The rules of the GpuDynamicBinding refusal family.</summary>
public enum GpuDynamicBindingRefusal
{
    /// <summary>Refuses a nonzero dynamic offset on a set with no dynamic layout element.</summary>
    DynamicElementMissing,
    /// <summary>Refuses a composed buffer offset that violates the backend's binding alignment.</summary>
    OffsetMisaligned,
    /// <summary>Refuses a uniform binding window that reaches outside its own frame-ring segment.</summary>
    WindowOutsideSegment,
}
