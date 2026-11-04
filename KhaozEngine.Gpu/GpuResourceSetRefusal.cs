namespace KhaozEngine.Gpu;

/// <summary>Why GpuResourceSet refused an operation.</summary>
public enum GpuResourceSetRefusal
{
    /// <summary>Refuses a binding window that does not fit within the buffer.</summary>
    BufferWindowOutsideResource,
    /// <summary>Refuses a set resource whose native handle was already released.</summary>
    NativeHandleMissing,
    /// <summary>Refuses a set whose resource count differs from its layout.</summary>
    ResourceCountMismatch,
    /// <summary>Refuses a resource whose type does not match the layout element.</summary>
    ResourceKindMismatch,
    /// <summary>Refuses a null resource in a set declaration.</summary>
    ResourceMissing,
    /// <summary>Refuses a staging texture as a shader texture binding.</summary>
    StagingTextureCannotBind,
    /// <summary>Refuses a texture lacking the usage required by the layout binding.</summary>
    TextureUsageMissing,
}
