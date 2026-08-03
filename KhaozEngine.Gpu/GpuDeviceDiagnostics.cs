namespace KhaozEngine.Gpu
{
    /// <summary>
    /// THE TWO FACTS A DEVICE CAN ONLY REPORT ABOUT ITSELF, read LIVE rather than captured at creation: whether it
    /// is running on a software rasterizer, and why it was lost if it has been. Surfaced on
    /// <see cref="IGpuDevice.Diagnostics"/> and <see cref="GpuDeviceContext.Diagnostics"/>, and carried into the
    /// telemetry session header by <see cref="GpuTelemetry"/>.
    /// <para>
    /// LIVE IS THE WHOLE REASON THIS IS A SEAM MEMBER rather than a pair of constructor arguments. A device loss
    /// happens at an arbitrary moment long after creation, so a value handed over when the device was made would
    /// always say the device was fine. Everything else the header carries about the GPU is fixed at creation and
    /// is passed as data, and these two are not.
    /// </para>
    /// <para>
    /// BOTH ARE NULLABLE, AND NULL IS "NOBODY ANSWERED" rather than "no". A backend that does not report the
    /// software-adapter flag is a different fact from one that reports false, and a capture that cannot tell those
    /// apart cannot say whether its performance numbers are comparable with another capture's. The default
    /// interface implementation returns exactly that: no answers, from a device that has none to give.
    /// </para>
    /// </summary>
    public readonly struct GpuDeviceDiagnostics
    {
        /// <summary>Build a diagnostics snapshot. Both arguments default to "not answered".</summary>
        public GpuDeviceDiagnostics(bool? softwareAdapter = null, string? deviceLossReason = null)
        {
            SoftwareAdapter = softwareAdapter;
            DeviceLossReason = deviceLossReason;
        }

        /// <summary>
        /// True when the device is running on a software rasterizer, false when it is not, null when the backend
        /// cannot say. On Direct3D11 this is <c>DXGI_ADAPTER_FLAG_SOFTWARE</c> off the adapter the device was
        /// actually created on, which is what makes it right on the path where nothing in the engine picked the
        /// adapter at all.
        /// </summary>
        public bool? SoftwareAdapter { get; }

        /// <summary>
        /// Why the device was LOST, or null while it is fine (which is nearly every session). On Direct3D11 this
        /// is <c>GetDeviceRemovedReason</c>'s answer as a stable token plus the call site that first noticed,
        /// read at that site because the removal HRESULT is sticky and every later call returns it too.
        /// </summary>
        public string? DeviceLossReason { get; }

        /// <summary>True when the device has reported a loss.</summary>
        public bool IsDeviceLost => DeviceLossReason != null;
    }
}
