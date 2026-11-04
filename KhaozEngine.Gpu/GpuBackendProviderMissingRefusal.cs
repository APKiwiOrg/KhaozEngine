namespace KhaozEngine.Gpu
{
    /// <summary>The registry wiring refusal for a missing graphics backend provider.</summary>
    public enum GpuBackendProviderMissingRefusal
    {
        /// <summary>Refuses a backend that has no registered provider.</summary>
        ProviderNotRegistered,
    }
}
