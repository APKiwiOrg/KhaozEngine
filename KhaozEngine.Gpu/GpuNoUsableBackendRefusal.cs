namespace KhaozEngine.Gpu
{
    /// <summary>The reasons device creation and its fallback could not produce a usable backend.</summary>
    public enum GpuNoUsableBackendRefusal
    {
        /// <summary>Refuses creation after the requested backend and its fallback both throw.</summary>
        RequestedCreationFailed,
        /// <summary>Refuses creation after an unsupported requested backend and a failed fallback.</summary>
        RequestedMachineUnsupported,
        /// <summary>Refuses creation after a stored preference has no registered provider and its fallback fails.</summary>
        RequestedProviderNotRegistered,
    }
}
