namespace KhaozEngine.Gpu;

/// <summary>Why GpuStagingMap refused an operation.</summary>
public enum GpuStagingMapRefusal
{
    /// <summary>Refuses a second map while the staging resource is mapped.</summary>
    AlreadyMapped,
    /// <summary>Refuses a fenced read map until its completion fence signals.</summary>
    CompletionNotSignaled,
    /// <summary>Refuses a staging or upload resource without the owning backend native capability.</summary>
    ForeignResource,
    /// <summary>Refuses a staging write without mapped backing memory.</summary>
    MappingMissing,
    /// <summary>Refuses an unmap when no map is open.</summary>
    NotMapped,
    /// <summary>Refuses mapping or reading a resource without the required staging usage.</summary>
    StagingUsageRequired,
}
