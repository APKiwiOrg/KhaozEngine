namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE ONE NATIVE CALL DECISION G3 MAKES AT A FAULT SITE: <c>ID3D11Device::GetDeviceRemovedReason</c>, behind
    /// an interface so <see cref="D3D11DeviceLossLatch"/> and everything above it are device-free.
    /// <para>
    /// ONE MEMBER, AND IT RETURNS AN <c>int</c> RATHER THAN A <c>Result</c>. The whole point of the seam is that
    /// no SharpGen or Vortice type reaches the latch, since the latch is the piece that has to be exercised on
    /// macOS: the HRESULT is a number, <see cref="D3D11DeviceLossCodes"/> reads it, and the native device converts
    /// once at the boundary (<c>device.DeviceRemovedReason.Code</c>).
    /// </para>
    /// <para>
    /// THE NATIVE DEVICE IMPLEMENTS THIS ITSELF, rather than there being a wrapper here. It already holds the
    /// <c>ID3D11Device</c> and the implementation is one expression, so a separate Windows type would be an object
    /// whose only job is forwarding. That wiring lands with the device row, which is also the row that puts the
    /// latch at its three call sites.
    /// </para>
    /// </summary>
    internal interface ID3D11RemovedReason
    {
        /// <summary>
        /// The device's removal reason, RIGHT NOW, as a raw HRESULT. <see cref="D3D11DeviceLossCodes.Ok"/> on a
        /// device that is fine.
        /// <para>
        /// Must never throw. A reason read that faults during a device loss would replace the diagnostic with a
        /// second, less informative failure at exactly the moment the first one mattered.
        /// </para>
        /// </summary>
        int GetDeviceRemovedReason();
    }
}
