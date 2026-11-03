namespace KhaozEngine.Gpu;

/// <summary>Why GpuCopyAlignment refused an operation.</summary>
public enum GpuCopyAlignmentRefusal
{
    /// <summary>Refuses a copy or readback offset not aligned to the portable byte boundary.</summary>
    UnalignedOffset,
}
