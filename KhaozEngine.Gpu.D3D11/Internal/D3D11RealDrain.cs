using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <c>KE_D3D11_REAL_DRAIN</c>, THE M2 KILL SWITCH (decision C6, section 13). Unlike
    /// <see cref="D3D11RecordModes"/>, which selects between two drivers that both ship, this one has a real
    /// default and an escape hatch: the drain is ON, and setting the variable to 0 restores the empty
    /// <c>WaitForIdle</c> the incumbent Direct3D 11 backend always had.
    /// <para>
    /// WHAT THE SWITCH IS FOR, and why it is temporary. Veldrid's <c>WaitForIdleCore</c> on Direct3D 11 was an
    /// empty method body, so every drain in the engine did nothing there, including one half of the
    /// only ordering guarantee the seam offers. That has never caused a known bug, because Direct3D 11 tracks
    /// resource hazards itself, defers destruction by reference counting and blocks in <c>Map</c> by definition,
    /// so the empty body is arguably correct-by-API. Making it real can therefore only ever be MORE conservative,
    /// which means the risk is performance and nothing else, which means it is measurable rather than latent. M2
    /// is that measurement, this is the lever a field session flips if it goes badly, and the exit criterion
    /// (total drain duration under 0.2 ms per frame across two consecutive soak builds) REMOVES this type.
    /// </para>
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any
    /// operating system, matching <see cref="D3D11RecordModes"/> and <c>GpuD3D11DeviceFlags</c>.
    /// </para>
    /// </summary>
    internal static class D3D11RealDrain
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized off values: <c>0</c>,
        /// <c>false</c>, <c>no</c>, <c>off</c>. Recognized on values: <c>1</c>, <c>true</c>, <c>yes</c>,
        /// <c>on</c>. Unset or empty is ON. All case-insensitive, whitespace trimmed.</summary>
        internal const string EnvVarName = "KE_D3D11_REAL_DRAIN";

        /// <summary>
        /// Whether <paramref name="envValue"/> leaves the real drain on. A non-blank value that is neither an on
        /// nor an off value comes back through <paramref name="unrecognizedValue"/> verbatim so the caller can
        /// WARN, and the default (on) is used.
        /// <para>
        /// The unrecognized case matters more here than for an ordinary setting. This variable exists so a field
        /// session can prove the drain is the cause of a regression, and a mistyped OFF that silently left the
        /// drain ON would produce a measurement that says the drain is innocent when the run never turned it off.
        /// </para>
        /// </summary>
        internal static bool Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return true;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "0": case "false": case "no": case "off":
                    return false;
                case "1": case "true": case "yes": case "on":
                    return true;
                default:
                    unrecognizedValue = envValue;
                    return true;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static bool FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized on/off value (1/true/yes/on, 0/false/no/off). "
                + "The native Direct3D 11 WaitForIdle stays a real fence drain, which is the default.";

        /// <summary>The INFO line for a run with the drain turned OFF, so a capture proves the lever was down
        /// rather than resting on the tester believing they set it. A run on the default says nothing, because a
        /// line on every session is a line nobody reads.</summary>
        internal static string DisabledDescription
            => $"The native Direct3D 11 WaitForIdle is a NO-OP for this run (from {EnvVarName}=0). It will not "
                + "wait for the GPU, matching the empty body the Veldrid Direct3D 11 backend has always had. "
                + "This is the M2 kill switch and it exists for the soak window only, so unset the variable to "
                + "go back to the real drain.";
    }
}
