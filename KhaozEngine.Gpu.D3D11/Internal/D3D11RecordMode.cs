using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Which recording driver the native Direct3D 11 backend runs.</summary>
    internal enum D3D11RecordMode
    {
        /// <summary>Decision R1, the default: seam calls become ops in an engine-owned CPU command stream and
        /// every native call happens inside <c>Submit</c>.</summary>
        Deferred = 0,

        /// <summary>Decision R2, the M1 fallback: seam calls reach the emitter as they are made, with no stream.
        /// The ring degrades to a map and unmap per flush when this driver is selected (work-breakdown row 8).
        /// </summary>
        Immediate = 1,
    }

    /// <summary>
    /// <c>KE_D3D11_RECORD</c>, THE M1 KILL SWITCH, which exists from the moment the recording model lands
    /// (sections 5.3 and 13). Milestone M1 measures end-to-end frame time on a real scene with both drivers
    /// built against the same emitter, and it gates exactly one thing: removing this variable and deleting the
    /// losing driver. Until that measurement is taken, BOTH drivers ship and neither is deleted, which is why a
    /// value here selects rather than merely enables.
    /// <para>
    /// The named risk M1 is about is NOT the memcpy. Under the deferred driver every native call bunches into
    /// the replay window, so record and driver-side consumption become two sequential phases instead of
    /// overlapping ones, and the driver-threading probe measured that the overlap HELPS. That is why the switch
    /// is a field lever rather than a build flag: a regression on the reporting machine is one environment
    /// variable away from an A/B on the same build.
    /// </para>
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any
    /// operating system, matching <see cref="GpuD3D11DeviceFlags"/>.
    /// </para>
    /// </summary>
    internal static class D3D11RecordModes
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values:
        /// <c>immediate</c> selects the immediate-emit driver, <c>deferred</c> and <c>stream</c> select the
        /// command-stream driver. Unset or empty is the command-stream driver. Case-insensitive, whitespace
        /// trimmed.</summary>
        internal const string EnvVarName = "KE_D3D11_RECORD";

        /// <summary>
        /// The driver <paramref name="envValue"/> asks for. A non-blank value that is not recognized comes back
        /// through <paramref name="unrecognizedValue"/> verbatim so the caller can WARN, and the default is used.
        /// A mistyped switch that silently does nothing is the failure worth spending a branch on here more than
        /// anywhere else in the backend: this variable exists to attribute a measurement to a driver, and a run
        /// that measured the wrong one and said so is how a number gets published and then retracted.
        /// </summary>
        internal static D3D11RecordMode Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return D3D11RecordMode.Deferred;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "immediate":
                    return D3D11RecordMode.Immediate;
                case "deferred": case "stream":
                    return D3D11RecordMode.Deferred;
                default:
                    unrecognizedValue = envValue;
                    return D3D11RecordMode.Deferred;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static D3D11RecordMode FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized recording driver (immediate, deferred, stream). "
                + "Using the command-stream driver, which is the default.";

        /// <summary>The INFO line for a run that is NOT on the default driver, so a capture proves which driver
        /// produced its numbers rather than resting on the tester believing they set the variable.</summary>
        internal static string ActiveDescription(D3D11RecordMode mode)
            => mode == D3D11RecordMode.Immediate
                ? $"The native Direct3D 11 backend is on the IMMEDIATE-EMIT driver (from {EnvVarName}=immediate). "
                    + "Seam calls issue native calls as they are recorded and there is no command stream. This is "
                    + "the M1 fallback driver, kept selectable until that measurement is taken."
                : $"The native Direct3D 11 backend is on the COMMAND-STREAM driver, the default. Recording issues "
                    + $"no native calls and the stream is replayed inside Submit. Set {EnvVarName}=immediate to "
                    + "A/B against the immediate-emit driver on the same build.";
    }
}
