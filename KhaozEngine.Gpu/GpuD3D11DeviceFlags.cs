using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The opt-in Direct3D11 device-creation flags the engine can be asked to add, and the environment gate that
    /// turns them on. Today there is exactly one: <c>D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS</c>,
    /// which tells the D3D11 runtime not to apply its own threading optimizations. It is a DIAGNOSTIC lever, not a
    /// setting: it exists so a tester chasing a driver-threading stall can prove whether those optimizations are
    /// part of the problem, and it can cost performance, which is why nothing turns it on by default.
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any OS.
    /// The flag is only ever reached on the Direct3D11 device-creation path in <see cref="GpuDeviceContext"/>.
    /// </para>
    /// </summary>
    public static class GpuD3D11DeviceFlags
    {
        /// <summary>The env var that opts in, following the engine's <c>KE_</c> convention (see
        /// <see cref="GpuBackendSelector.EnvVarName"/>). Recognized on values: <c>1</c>, <c>true</c>, <c>yes</c>,
        /// <c>on</c>. Recognized off values: unset, empty, <c>0</c>, <c>false</c>, <c>no</c>, <c>off</c>. All
        /// case-insensitive, whitespace trimmed.</summary>
        public const string EnvVarName = "KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS";

        /// <summary>
        /// <c>D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS</c>, as a raw value ready for
        /// <c>D3D11DeviceOptions.DeviceCreationFlags</c> (Veldrid takes a <see cref="uint"/> there and casts it
        /// straight to its own flags enum).
        /// <para>
        /// Taken FROM Vortice's enum rather than written out, so the number cannot be wrong and cannot drift. It is
        /// a compile-time constant expression, so the compiler folds it to a literal and no Vortice type is named
        /// in the emitted code: the assembly stays unloaded off Windows exactly as
        /// <c>Internal/D3D11ThreadingProbe</c> requires. The matching test pins it to the documented Windows SDK
        /// value, so a future Vortice rename or repoint fails a test instead of silently changing which flag the
        /// engine sets. Hand-writing it would have been wrong: the plausible guess, 0x800, is VideoSupport.
        /// </para>
        /// </summary>
        public const uint PreventInternalThreadingOptimizations =
            (uint)Vortice.Direct3D11.DeviceCreationFlags.PreventInternalThreadingOptimizations;

        /// <summary>The INFO line logged when the flag is active, so a tester's log PROVES the lever was on rather
        /// than the tester believing they set it.</summary>
        public static string ActiveDescription { get; } =
            $"D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS is ACTIVE for this device "
            + $"(0x{PreventInternalThreadingOptimizations:X}, from {EnvVarName}). The Direct3D11 runtime will not "
            + "apply its internal threading optimizations. This is a diagnostic lever and it can cost performance, "
            + "so unset the variable to go back to the default.";

        /// <summary>
        /// The device-creation flags <paramref name="envValue"/> asks for: <see cref="PreventInternalThreadingOptimizations"/>
        /// when it is a recognized on value, otherwise 0. A non-blank value that is neither an on nor an off value
        /// comes back through <paramref name="unrecognizedValue"/> (verbatim, original case) so the caller can warn.
        /// A mistyped gate that silently does nothing is the failure mode worth spending a branch on: it is
        /// indistinguishable from the default, so a whole test session can be spent proving nothing.
        /// </summary>
        public static uint Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return 0u;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on":
                    return PreventInternalThreadingOptimizations;
                case "0": case "false": case "no": case "off":
                    return 0u;
                default:
                    unrecognizedValue = envValue;
                    return 0u;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        public static uint FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing. Names what was typed and what
        /// would have worked.</summary>
        public static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized on/off value (1/true/yes/on, 0/false/no/off). "
                + "Leaving the Direct3D11 internal threading optimizations at their default.";
    }
}
