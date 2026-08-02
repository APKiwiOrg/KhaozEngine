namespace KhaozEngine.Gpu
{
    /// <summary>
    /// What an <see cref="IGpuBackendProvider"/> hands back from a creation call: the device itself, plus the two
    /// diagnostic values only the provider is in a position to produce.
    /// <para>
    /// The threading pair is carried rather than re-derived because a natively created device has no Veldrid
    /// <c>GraphicsDevice</c> for <c>D3D11ThreadingProbe</c> to read a raw <c>ID3D11Device</c> pointer off. The
    /// provider owns that pointer, so it runs the probe's raw-pointer entry itself and passes both halves through:
    /// <paramref name="ThreadingCaps"/> null means "no answer", and <paramref name="ThreadingProbeFailure"/> is the
    /// reason when the probe was ATTEMPTED and did not answer (null when it answered, and null when there was
    /// nothing to ask). Those are exactly the two values <see cref="GpuDeviceContext"/> needs to log the same
    /// threading line, with the same WARN, that the Veldrid path logs.
    /// </para>
    /// </summary>
    /// <param name="Device">The created device. A provider that cannot create one THROWS instead of returning
    /// nothing, so the failure carries a reason the fallback can log.</param>
    /// <param name="ThreadingCaps">The driver threading capabilities, or null when there was no answer.</param>
    /// <param name="ThreadingProbeFailure">Why the threading probe produced no answer, or null.</param>
    public readonly record struct GpuProviderDevice(
        IGpuDevice Device,
        GpuThreadingCaps? ThreadingCaps,
        string? ThreadingProbeFailure);

    /// <summary>
    /// Everything a provider needs to create a WINDOWED device: the platform-native window handle the windowing
    /// package already built, the backbuffer size, and whether to present on the vertical blank.
    /// <para>
    /// A record rather than four parameters so a later backend need can be added in one place instead of on every
    /// implementation at once, and so the window handle keeps travelling as the single opaque value
    /// <see cref="GpuWindowHandle"/> already is.
    /// </para>
    /// </summary>
    /// <param name="Window">The native window handle to present to.</param>
    /// <param name="Width">Backbuffer width in pixels.</param>
    /// <param name="Height">Backbuffer height in pixels.</param>
    /// <param name="SyncToVerticalBlank">True to present on the vertical blank, false for immediate presentation.</param>
    public readonly record struct GpuWindowedDeviceRequest(
        GpuWindowHandle Window,
        uint Width,
        uint Height,
        bool SyncToVerticalBlank);

    /// <summary>
    /// One graphics backend that lives OUTSIDE <c>KhaozEngine.Gpu</c>, in its own opt-in package. Implemented by
    /// the package (<c>KhaozEngine.Gpu.D3D11</c> is the first) and handed to
    /// <see cref="GpuBackendProviders.Register"/> by the consuming app at startup, which is what lets
    /// <see cref="GpuDeviceContext"/> create a backend it cannot reference: the seam package cannot depend on a
    /// backend package without a cycle, so the backend arrives as data instead.
    /// <para>
    /// See section 4.1 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>. Registration is EXPLICIT
    /// on purpose. A <c>[ModuleInitializer]</c> in the backend package was rejected because the CLR loads an
    /// assembly lazily on first type reference, so a package reference with no static type use does not guarantee
    /// the initializer ever runs, and that failure is silent and machine-dependent. Reflection probing by assembly
    /// name was rejected too: trim and AOT hostile, invisible to the architecture tests, and it turns a missing
    /// reference into a runtime string mismatch.
    /// </para>
    /// </summary>
    public interface IGpuBackendProvider
    {
        /// <summary>
        /// Whether THIS MACHINE can actually run the backend, as a functional probe rather than a guess. The
        /// answer is cached per backend for the process lifetime by
        /// <see cref="GpuBackendSelector.IsBackendSupported"/>, which is the only caller, so the probe may create
        /// and destroy a throwaway device to find out.
        /// <para>
        /// It must NEVER throw: a probe that blows up is reported as unsupported, because "we could not even ask"
        /// and "no" are the same answer to the settings screen and the fallback that consume it. Implementations
        /// answer for the machine only. A missing registration is a different fact entirely, is never asked here,
        /// and is answered by <see cref="GpuBackendProviders.Require"/> throwing (decision I2).
        /// </para>
        /// <para>
        /// For the native Direct3D11 backend this checks the two HARD device requirements it cannot work without:
        /// <c>ConstantBufferOffsetting</c>, because every constant-buffer bind goes through
        /// <c>*SetConstantBuffers1</c> with an explicit first constant and constant count (R7), and
        /// <c>MapNoOverwriteOnDynamicConstantBuffer</c>, because the per-frame uniform ring is mapped
        /// <c>MAP_WRITE_NO_OVERWRITE</c> for the whole record phase (U2). A machine missing either one cannot run
        /// the backend at all, and finding that out here is what routes it through the reported fallback rather
        /// than through a crash on the first frame.
        /// </para>
        /// </summary>
        bool IsSupported();

        /// <summary>
        /// Create a windowed device for <paramref name="request"/>, with its swapchain, and probe whatever
        /// diagnostics <see cref="GpuProviderDevice"/> carries. Called inside
        /// <see cref="GpuDeviceContext"/>'s process-wide creation gate, so implementations need no lifecycle lock
        /// of their own. Throws on failure, and the caller decides whether that failure falls back.
        /// </summary>
        GpuProviderDevice CreateForWindow(in GpuWindowedDeviceRequest request);

        /// <summary>
        /// Create an offscreen device with no swapchain, for the headless snapshot and golden paths. Called inside
        /// the same creation gate. Throws on failure, and headless creation never falls back: a headless run that
        /// quietly changed backend would file its golden images under the wrong backend.
        /// </summary>
        GpuProviderDevice CreateHeadless();
    }
}
