namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// The one teardown-ordering hook <see cref="GpuDeviceContext"/> needs from the device it owns: latch the
    /// device as destroyed, so a resource wrapper disposed AFTER the device no-ops instead of calling into
    /// freed driver objects. Called inside the context's process-wide lifecycle gate, immediately before the
    /// underlying native device is destroyed.
    /// <para>
    /// This is an interface rather than a cast on purpose. Disposal used to read
    /// <c>((VeldridGpuDevice)GpuDevice).MarkDeviceDisposed()</c>, on a device deleted in 18.0.0, and since the
    /// context is the engine's only device-creation path, that one cast is what made the Veldrid wrapper the only
    /// <see cref="IGpuDevice"/> a consumer could ever be handed: any other implementation would have thrown
    /// <see cref="System.InvalidCastException"/> at teardown. See decision P3 and section 4.2 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>.
    /// </para>
    /// <para>
    /// Implementing it is OPTIONAL. A device with no liveness token of its own (nothing that could outlive it)
    /// simply does not implement this, and the context skips the latch rather than requiring an empty method.
    /// </para>
    /// </summary>
    internal interface IGpuDeviceLifecycle
    {
        /// <summary>
        /// Latch the device as destroyed. Must be idempotent and must never throw: it runs inside the lifecycle
        /// gate on a teardown path that has no way to report a failure and nothing useful to do with one.
        /// </summary>
        void MarkDeviceDisposed();
    }
}
