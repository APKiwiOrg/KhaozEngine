using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DEVICE-LOSS VOCABULARY, as plain <c>int</c> HRESULTs with names on them. Decision G3 needs two
    /// questions answered at every check site: is this HRESULT a device loss, and what does
    /// <c>GetDeviceRemovedReason</c>'s answer mean. Both are pure, so both run on macOS.
    /// <para>
    /// THE VALUES ARE WRITTEN OUT RATHER THAN TAKEN FROM VORTICE, and that is the opposite of what
    /// <c>GpuD3D11DeviceFlags</c> and <see cref="D3D11ShaderDebug"/> do, for a reason that is specific to DXGI.
    /// Those two take their values from Vortice enums, which are compile-time constants the compiler folds to
    /// literals with no type left in the emitted code. DXGI's result codes are not enum members: they are
    /// <c>static readonly</c> SharpGen <c>Result</c> values, so naming one would emit a field access against a
    /// Vortice type and put the interop on the load path on macOS and Linux, which is the one thing the whole
    /// package boundary exists to prevent. The values below are the documented Windows SDK ones and are ABI
    /// stable, since a shipped HRESULT can never change.
    /// </para>
    /// <para>
    /// STICKINESS IS WHY ANY OF THIS EXISTS. <c>DXGI_ERROR_DEVICE_REMOVED</c> is returned by every subsequent call
    /// on a dead device, so the FIRST site to notice it is nowhere near the cause, which is why all 25 stacks on
    /// #423 pointed at a texture view constructor rather than at whatever corrupted the device. Reading
    /// <see cref="D3D11DeviceLossLatch"/>'s <c>GetDeviceRemovedReason</c> at the first site that notices is the
    /// only moment the real cause is still available.
    /// </para>
    /// </summary>
    internal static class D3D11DeviceLossCodes
    {
        /// <summary><c>S_OK</c>. Also what <c>GetDeviceRemovedReason</c> answers on a device that is fine.</summary>
        internal const int Ok = 0;

        /// <summary><c>DXGI_ERROR_INVALID_CALL</c>. Not a device loss, listed because it is the neighbour that
        /// arrives from the same calls and would otherwise be mistaken for one.</summary>
        internal const int InvalidCall = unchecked((int)0x887A0001);

        /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c>. The adapter is gone: a driver update, a hardware removal,
        /// or a fault the driver responded to by tearing the device down.</summary>
        internal const int DeviceRemoved = unchecked((int)0x887A0005);

        /// <summary><c>DXGI_ERROR_DEVICE_HUNG</c>. The application's own commands caused the hang. Only ever a
        /// <c>GetDeviceRemovedReason</c> answer, never the HRESULT a call returns, and it is the one that means
        /// the bug is ours.</summary>
        internal const int DeviceHung = unchecked((int)0x887A0006);

        /// <summary><c>DXGI_ERROR_DEVICE_RESET</c>. The device failed for a reason not attributable to this
        /// application, typically another process hanging the GPU.</summary>
        internal const int DeviceReset = unchecked((int)0x887A0007);

        /// <summary><c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c>. The driver says the fault is its own.</summary>
        internal const int DriverInternalError = unchecked((int)0x887A0020);

        /// <summary>
        /// Whether <paramref name="hresult"/> is the device going away. Exactly the two codes decision G3 names,
        /// and deliberately not "any failure": a check site that latched on every failing HRESULT would kill the
        /// device on an ordinary <c>DXGI_ERROR_INVALID_CALL</c>, and everything after that would be a no-op with
        /// no explanation anywhere.
        /// <para>
        /// <c>DXGI_ERROR_DEVICE_HUNG</c> and <c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c> are absent on purpose. They
        /// are answers <c>GetDeviceRemovedReason</c> gives, not codes a call returns, so treating them as a
        /// trigger would be checking for something that cannot arrive here.
        /// </para>
        /// </summary>
        internal static bool IsDeviceLoss(int hresult)
            => hresult == DeviceRemoved || hresult == DeviceReset;

        /// <summary>Whether an HRESULT is a failure at all, which is the sign bit and nothing more. The same
        /// reading <see cref="D3D11Swapchain.Present"/> already documents at its own site, so an occluded present
        /// (a success that presented nothing) is not mistaken for a fault.</summary>
        internal static bool IsFailure(int hresult) => hresult < 0;

        /// <summary>
        /// What <paramref name="hresult"/> means, in a sentence a bug report can carry. The name comes first,
        /// because that is what a reader searches on, and the hex is always present, because an unrecognized code
        /// still has to be reportable.
        /// </summary>
        internal static string Describe(int hresult) => hresult switch
        {
            Ok => "S_OK (the device reports no removal reason)",
            DeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED (0x887A0005), the adapter went away: a driver update, a "
                + "hardware removal, or a fault the driver answered by tearing the device down",
            DeviceHung => "DXGI_ERROR_DEVICE_HUNG (0x887A0006), the application's own commands hung the GPU. This "
                + "is the one that means the fault is in what the engine submitted",
            DeviceReset => "DXGI_ERROR_DEVICE_RESET (0x887A0007), the device failed for a reason outside this "
                + "application, typically another process hanging the GPU",
            DriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR (0x887A0020), the driver reports the fault "
                + "as its own",
            InvalidCall => "DXGI_ERROR_INVALID_CALL (0x887A0001), which is not a device loss at all",
            _ => "an unrecognized HRESULT (0x"
                + hresult.ToString("X8", CultureInfo.InvariantCulture) + ")",
        };

        /// <summary>The short token the telemetry session header carries, which is the NAME rather than the
        /// sentence: a header field is grouped and counted across captures, so it has to be a stable token, and
        /// the sentence goes in the session log beside it.</summary>
        internal static string Token(int hresult) => hresult switch
        {
            Ok => "S_OK",
            DeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
            DeviceHung => "DXGI_ERROR_DEVICE_HUNG",
            DeviceReset => "DXGI_ERROR_DEVICE_RESET",
            DriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
            InvalidCall => "DXGI_ERROR_INVALID_CALL",
            _ => "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture),
        };
    }
}
