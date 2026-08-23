using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G4's DEVICE HALF OF <c>KE_D3D11_DEBUG</c>: whether device creation adds
    /// <c>D3D11_CREATE_DEVICE_DEBUG</c>, and what to do when the machine has no debug layer installed.
    /// <see cref="D3D11ShaderDebug"/> is the same variable's shader half (which FXC flags a shader is compiled
    /// with). ONE VARIABLE, TWO EFFECTS, deliberately: a session debugging a Direct3D problem wants both, and
    /// remembering two names to get one answer is how a capture ends up taken with half the instrumentation on.
    /// <para>
    /// THIS IS THE CHEAPEST DIAGNOSTIC IN THE DESIGN. The engine hardcoded Veldrid's debug flag false, so there
    /// was no way at all to get debug-layer output out of a diagnostic run, and the 25
    /// <c>DEVICE_REMOVED</c> stacks on #423 are exactly the shape of report the debug layer answers in one
    /// session. The layer costs real performance, which is why nothing turns it on by default and why the INFO
    /// line below exists: a capture must PROVE the lever was on rather than resting on the tester believing they
    /// set it.
    /// </para>
    /// <para>
    /// THE TWO READERS OF THIS VARIABLE MUST NEVER DISAGREE. They parse the same on and off value sets, and the
    /// parse is duplicated rather than shared because each lever type in this package is self-contained and
    /// independently testable, which is the established shape here. <c>D3D11DebugLeverTests</c> pins the pair
    /// against the same value table, so a change to one that is not made to the other fails a test rather than
    /// producing a session with debug shaders and no debug layer.
    /// </para>
    /// <para>
    /// The flag value is taken FROM Vortice's enum rather than written out, as a <c>const uint</c> compile-time
    /// constant expression, so the compiler folds it to a literal and no Vortice type is named in the emitted
    /// code. That is what keeps the interop off the load path on macOS and Linux while a plain <c>uint</c>
    /// travels to the creation call. Everything here is pure except <see cref="FromEnvironment"/>.
    /// </para>
    /// </summary>
    internal static class D3D11DebugLayer
    {
        /// <summary>The env var, which is <see cref="D3D11ShaderDebug.EnvVarName"/> and is named through it rather
        /// than repeated, so the two halves cannot drift to different spellings.</summary>
        internal const string EnvVarName = D3D11ShaderDebug.EnvVarName;

        /// <summary><c>D3D11_CREATE_DEVICE_DEBUG</c>, ready to be OR'd into the creation flags beside
        /// <c>GpuD3D11DeviceFlags.PreventInternalThreadingOptimizations</c>.</summary>
        internal const uint CreateDeviceDebug = (uint)Vortice.Direct3D11.DeviceCreationFlags.Debug;

        /// <summary>
        /// <c>DXGI_ERROR_SDK_COMPONENT_MISSING</c>. What <c>D3D11CreateDevice</c> returns when
        /// <see cref="CreateDeviceDebug"/> is requested on a machine without the Windows graphics tools installed,
        /// which is most machines that are not a developer's. Written out rather than taken from Vortice because
        /// the DXGI result codes are <c>static readonly</c> SharpGen values rather than compile-time constants, so
        /// naming one here would put the interop on the load path off Windows. The value is the documented Windows
        /// SDK one and is ABI-stable.
        /// </summary>
        internal const int SdkComponentMissing = unchecked((int)0x887A002D);

        /// <summary>
        /// The device-creation flags <paramref name="envValue"/> asks for: <see cref="CreateDeviceDebug"/> on a
        /// recognized on value, otherwise 0. A non-blank value that is neither an on nor an off value comes back
        /// through <paramref name="unrecognizedValue"/> verbatim so the caller can WARN.
        /// </summary>
        internal static uint Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return 0u;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on":
                    return CreateDeviceDebug;
                case "0": case "false": case "no": case "off":
                    return 0u;
                default:
                    unrecognizedValue = envValue;
                    return 0u;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static uint FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>
        /// Whether a failed creation should be RETRIED without the debug flag. True only for
        /// <see cref="SdkComponentMissing"/> and only when the debug flag was actually requested, so an ordinary
        /// creation failure is never retried into a second, more confusing one.
        /// <para>
        /// Retrying is the right answer rather than failing, because the alternative is that setting
        /// <c>KE_D3D11_DEBUG=1</c> on a machine without the graphics tools stops the app starting, and the person
        /// who set it is by definition mid-diagnosis. The WARN that goes with the retry is what keeps that from
        /// being silent, and it names the exact thing to install.
        /// </para>
        /// </summary>
        internal static bool ShouldRetryWithoutDebugLayer(uint requestedFlags, int creationHresult)
            => (requestedFlags & CreateDeviceDebug) != 0 && creationHresult == SdkComponentMissing;

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized on/off value (1/true/yes/on, 0/false/no/off). The "
                + "Direct3D 11 debug layer stays off and shaders stay compiled at optimization level 3, so this "
                + "run carries none of the instrumentation the variable was set to get.";

        /// <summary>The WARN body for a debug-layer request this machine cannot satisfy, naming the fix.</summary>
        internal static string UnavailableWarning()
            => $"{EnvVarName} asked for the Direct3D 11 debug layer and this machine does not have it installed "
                + "(DXGI_ERROR_SDK_COMPONENT_MISSING). The device was created WITHOUT it, so this run reports no "
                + "debug-layer messages. Install the Graphics Tools optional feature on Windows to get them. "
                + "Shader debug information is unaffected and is still on.";

        /// <summary>The INFO line for a run WITH the layer, so a capture proves the lever was on. A run on the
        /// default says nothing, because a line on every session is a line nobody reads.</summary>
        internal static string ActiveDescription
            => $"The Direct3D 11 debug layer is ACTIVE for this device (from {EnvVarName}), and its messages are "
                + "pumped into this log at a rate limit, with corruption and error severities raised to WARN. "
                + "Expect a large performance cost. Unset the variable to go back to the default.";
    }
}
