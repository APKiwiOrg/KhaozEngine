namespace KhaozEngine.Gpu;

/// <summary>The range rules for buffer writes and copies.</summary>
public enum GpuBufferTransferRefusal
{
    /// <summary>Refuses a write or copy window outside the buffer's logical size.</summary>
    WindowOutsideBuffer,
}
