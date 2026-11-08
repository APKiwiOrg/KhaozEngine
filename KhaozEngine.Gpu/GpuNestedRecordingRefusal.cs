namespace KhaozEngine.Gpu
{
    /// <summary>The portable per-device GPU recording refusal.</summary>
    public enum GpuNestedRecordingRefusal
    {
        /// <summary>Refuses a second recording while the device already has an open recording.</summary>
        RecordingAlreadyOpen,
    }
}
