namespace KhaozEngine.Gpu;

/// <summary>Why GpuResourceLayout refused an operation.</summary>
public enum GpuResourceLayoutRefusal
{
    /// <summary>Refuses a dynamic offset on a resource kind the backend cannot rebase.</summary>
    DynamicOffsetUnsupportedForKind,
}
