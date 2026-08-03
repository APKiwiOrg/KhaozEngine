using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <c>KE_D3D11_DEBUG</c> AS THE SHADER PATH READS IT: which FXC flags a shader is compiled with. Decision S1
    /// says <c>OptimizationLevel3</c> normally, and a debug build under this variable. Decision G4 gives the same
    /// variable a second job (the Direct3D debug layer plus the info-queue pump), which lands with the diagnostics
    /// row. One variable, two effects, deliberately: a session debugging a Direct3D problem wants both, and
    /// remembering two names to get one answer is how a capture ends up taken with half the instrumentation on.
    ///
    /// <para>
    /// THE DEBUG SET IS <c>Debug | SkipOptimization</c>, WHICH IS ONE FLAG MORE THAN THE DESIGN WROTE, and the
    /// extra one is the one that does the work. <c>D3DCOMPILE_DEBUG</c> alone attaches debug information while
    /// leaving the optimizer at its default level, so a RenderDoc or PIX capture shows source that no longer
    /// matches the instructions and stepping lands on the wrong lines, which is the entire thing the flag was set
    /// to get. <c>D3DCOMPILE_SKIP_OPTIMIZATION</c> is what makes the mapping usable. Nothing shipped runs under
    /// this: goldens, CI and every ordinary session compile at <see cref="Optimized"/>.
    /// </para>
    /// <para>
    /// THE FLAGS ARE PART OF THE CACHE KEY, so a debug session and an ordinary one never see each other's
    /// compiled bytes. That is not a nicety: a cached optimized module served to a debug session would present a
    /// capture with no debug information and no explanation for it.
    /// </para>
    /// <para>
    /// The values are taken FROM Vortice's enum rather than written out, so the numbers cannot be wrong and cannot
    /// drift, and they are <c>const uint</c> compile-time constant expressions, so the compiler folds them to
    /// literals and no Vortice type is named in the emitted code. That is the same shape
    /// <c>GpuD3D11DeviceFlags.PreventInternalThreadingOptimizations</c> uses, and it is what keeps the interop off
    /// the load path on macOS and Linux while a plain <c>uint</c> travels to the FXC call. Everything here is pure
    /// except <see cref="FromEnvironment"/>, so the parse is headless-testable on any operating system.
    /// </para>
    /// </summary>
    internal static class D3D11ShaderDebug
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized on values: <c>1</c>,
        /// <c>true</c>, <c>yes</c>, <c>on</c>. Recognized off values: unset, empty, <c>0</c>, <c>false</c>,
        /// <c>no</c>, <c>off</c>. All case-insensitive, whitespace trimmed.</summary>
        internal const string EnvVarName = "KE_D3D11_DEBUG";

        /// <summary><c>D3DCOMPILE_OPTIMIZATION_LEVEL3</c>. What every shipped compile uses.</summary>
        internal const uint Optimized = (uint)Vortice.D3DCompiler.ShaderFlags.OptimizationLevel3;

        /// <summary><c>D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION</c>. What a
        /// <c>KE_D3D11_DEBUG</c> session gets, so a capture's disassembly maps back to the emitted HLSL.</summary>
        internal const uint DebugBuild = (uint)(Vortice.D3DCompiler.ShaderFlags.Debug
            | Vortice.D3DCompiler.ShaderFlags.SkipOptimization);

        /// <summary>
        /// The FXC flags <paramref name="envValue"/> asks for. A non-blank value that is neither an on nor an off
        /// value comes back through <paramref name="unrecognizedValue"/> verbatim so the caller can WARN, and the
        /// default (optimized) is used.
        /// <para>
        /// The unrecognized case earns its branch here for the reason it does everywhere else in this package: a
        /// mistyped debug gate that silently compiled optimized is indistinguishable from a correct run, so a
        /// whole capture session can be spent looking at a disassembly that was never going to line up.
        /// </para>
        /// </summary>
        internal static uint Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return Optimized;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on":
                    return DebugBuild;
                case "0": case "false": case "no": case "off":
                    return Optimized;
                default:
                    unrecognizedValue = envValue;
                    return Optimized;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static uint FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized on/off value (1/true/yes/on, 0/false/no/off). "
                + "Shaders stay compiled at optimization level 3, which is the default, so a capture taken now "
                + "carries no shader debug information.";

        /// <summary>The INFO line for a run compiling debug shaders, so a capture PROVES the lever was on rather
        /// than resting on the tester believing they set it. A run on the default says nothing, because a line on
        /// every session is a line nobody reads.</summary>
        internal static string DebugDescription
            => $"Native Direct3D 11 shaders are compiled with debug information and no optimization for this run "
                + $"(from {EnvVarName}). Expect slower shaders and a disassembly that maps back to the emitted "
                + "HLSL. Unset the variable to go back to optimization level 3.";
    }
}
