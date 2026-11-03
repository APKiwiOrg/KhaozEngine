namespace KhaozEngine.Gpu;

/// <summary>Why GpuResourceOwnership refused an operation.</summary>
public enum GpuResourceOwnershipRefusal
{
    /// <summary>Refuses a resource created by a different device of the same backend.</summary>
    DifferentDevice,
    /// <summary>Refuses a resource originating from a different GPU backend.</summary>
    ForeignBackend,
    /// <summary>Refuses a required resource that was not supplied.</summary>
    MissingResource,
    /// <summary>Refuses use of a resource whose native handle has been released.</summary>
    NativeHandleReleased,
    /// <summary>Refuses a submitted command list or fence that is not owned by the submitting backend or device.</summary>
    ResourceNotOwnedByDevice,
}
