namespace KhaozEngine.Gpu.Metal;

/// <summary>Why MetalStagingArena refused an operation.</summary>
public enum MetalStagingArenaRefusal
{
    /// <summary>Refuses a nil native staging block allocation.</summary>
    BlockAllocationFailed,
    /// <summary>Refuses an arena lease size not aligned to the native copy boundary.</summary>
    LeaseSizeUnaligned,
}
