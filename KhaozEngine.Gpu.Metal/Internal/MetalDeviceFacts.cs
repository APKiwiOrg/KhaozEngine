namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// Everything the machine probe reads off ONE <c>MTLDevice</c>, as plain data with no Objective-C handle
    /// anywhere in it.
    /// <para>
    /// The split is what makes the probe's decision testable, and it is phase 3's shape reused rather than
    /// re-derived. Reading these values needs macOS and a real device, which two of the three
    /// <c>ci.yml</c>-adjacent legs do not have. DECIDING on them needs nothing, so the decision lives in
    /// <see cref="MetalDeviceRequirements"/> over this struct and is driven device-free from fabricated values,
    /// one requirement at a time, on every leg including the ones that could never produce hardware to fail it.
    /// </para>
    /// <para>
    /// It is a SNAPSHOT rather than a live view: the probe releases the device before the decision is taken, so
    /// nothing here may hold anything that dies with it. That is why <see cref="DeviceName"/> is a managed copy
    /// of the <c>NSString</c> rather than the pointer it came from.
    /// </para>
    /// </summary>
    /// <param name="DeviceCreated">Whether <c>MTLCreateSystemDefaultDevice()</c> returned anything at all. False
    /// is the answer on a Mac whose Metal device cannot be created, and it is the floor the incumbent's
    /// <c>MTLGraphicsDevice.GetIsSupported</c> stops at (M-N4).</param>
    /// <param name="DeviceName">The device's own <c>-name</c>, which is what
    /// <c>GpuCapabilities.DeviceName</c> parity depends on. Never null, and empty when the device reports
    /// nothing readable, which is a refusal rather than a cosmetic gap.</param>
    /// <param name="HighestAppleFamily">The highest <c>MTLGPUFamilyApple</c><i>n</i> the device answers yes to,
    /// or 0 for none. Apple silicon answers a run of them, an Intel Mac answers none.</param>
    /// <param name="SupportsMac2">Whether the device answers <c>MTLGPUFamilyMac2</c>, which is the family every
    /// Mac GPU on a supported macOS reports and which an Apple silicon device reports as well.</param>
    /// <param name="SupportsCommon1">Whether the device answers <c>MTLGPUFamilyCommon1</c>, the baseline every
    /// Metal device shares. Read for the diagnostic rather than for the gate, so a device that somehow answers
    /// nothing at all is distinguishable in a log line from one that simply sits below the floor.</param>
    /// <param name="BufferOffsetAlignment">The device's own minimum buffer-offset alignment in bytes, which
    /// M-M3's 256 stride has to be a multiple of. See
    /// <see cref="MetalDeviceFactsReader.ReadBufferOffsetAlignment"/> for which selector produced it and why Metal
    /// exposes no constant-buffer-specific query to ask instead. Zero means nothing answered.</param>
    /// <param name="BufferOffsetAlignmentSource">The selector the value above came from, carried into the
    /// refusal message so a rejected machine names the read rather than only the number.</param>
    /// <param name="SupportsTextureSampleCount1">Whether <c>-supportsTextureSampleCount:</c> answers yes for 1,
    /// which is the walk M-C3's <c>MaxMsaaSampleCount</c> read starts from.</param>
    internal readonly record struct MetalDeviceFacts(
        bool DeviceCreated,
        string DeviceName,
        int HighestAppleFamily,
        bool SupportsMac2,
        bool SupportsCommon1,
        nuint BufferOffsetAlignment,
        string BufferOffsetAlignmentSource,
        bool SupportsTextureSampleCount1);
}
