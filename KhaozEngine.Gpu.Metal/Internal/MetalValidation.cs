using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>What <c>KE_METAL_VALIDATION</c> was understood to be asking for (decision M-G3). A LADDER of
    /// increasing cost rather than a flag set, so a session picks a level rather than composing options that can
    /// disagree.</summary>
    internal enum MetalValidationMode
    {
        /// <summary>Unset, blank or an off value. The default, and the only rung with no runtime cost.</summary>
        Off = 0,

        /// <summary>Metal API validation, which the runtime arms from <c>MTL_DEBUG_LAYER</c>. Encoder-state,
        /// argument-range and pipeline-compatibility errors.</summary>
        On = 1,

        /// <summary><see cref="On"/> plus in-shader bounds checking, from <c>MTL_SHADER_VALIDATION</c>. The
        /// scheduled run's rung, because it is the expensive one.</summary>
        Shaders = 2,
    }

    /// <summary>
    /// DECISION M-G3's KNOB: <c>KE_METAL_VALIDATION</c>, parsed, and everything the parse has to say about
    /// itself. Pure, so the whole ladder is decided under <c>dotnet test</c> on a machine with no Metal.
    ///
    /// <para><b>THE KNOB CANNOT ARM ANYTHING, AND THAT IS MEASURED RATHER THAN ASSUMED.</b> Metal API validation
    /// is a PROCESS-LAUNCH mechanism: the runtime reads <c>MTL_DEBUG_LAYER</c> and <c>MTL_SHADER_VALIDATION</c>
    /// out of the environment before the first device exists, and there is no API to turn it on afterwards. The
    /// design refused to assert either way and sent it to a row-1 spike with a control. The answer was NO: an
    /// in-process native <c>setenv("MTL_DEBUG_LAYER", "1")</c> ahead of any Metal use left the device class
    /// <c>AGXG14CDevice</c>, while the same run launched with the variable already set got <c>MTLDebugDevice</c>.
    /// So the instrument is sound and the mechanism is not, and this row takes the fallback section 3.1 names: a
    /// job-level variable in CI and a documented prefix locally.</para>
    ///
    /// <para><b>SO THE KNOB'S JOB IS TO REPORT, AND REPORTING IS NOT NOTHING.</b> It says which tier is armed, it
    /// says so from the same session log a golden failure is read out of, and it WARNS when a tester asked for a
    /// tier the process cannot have. A run that believes it is validating and is not produces a clean result that
    /// proves nothing, which is the single worst outcome available from a diagnostic lever.</para>
    ///
    /// <para><b>AND THE REPORT IS CHECKED AGAINST THE DEVICE ITSELF</b>, through the same control the spike used:
    /// a validated device really is a different Objective-C class. <see cref="LooksLikeADebugDevice"/> is that
    /// check, so the log line says what the runtime actually did rather than what the environment implied.</para>
    /// </summary>
    internal static class MetalValidation
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values: <c>0</c>,
        /// <c>1</c> and <c>shaders</c>, plus the usual on/off spellings. Case-insensitive, trimmed.</summary>
        internal const string EnvVarName = "KE_METAL_VALIDATION";

        /// <summary>The process-level variable the Metal runtime reads for API validation, at launch.</summary>
        internal const string DebugLayerVar = "MTL_DEBUG_LAYER";

        /// <summary>The process-level variable the Metal runtime reads for shader validation, at launch.</summary>
        internal const string ShaderValidationVar = "MTL_SHADER_VALIDATION";

        /// <summary>
        /// What <paramref name="envValue"/> asks for, with <paramref name="unrecognizedValue"/> set verbatim
        /// (quotes, stray spaces and all) when the value was neither blank nor understood.
        /// <para>
        /// There is deliberately NO "anything else means on" reading, unlike
        /// <see cref="MetalDeviceSelection"/>'s substring arm. A device name is free text by nature so an
        /// unrecognized value there is still meaningful, while this knob has three values and a fourth is a typo.
        /// </para>
        /// </summary>
        internal static MetalValidationMode Parse(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return MetalValidationMode.Off;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "0": case "false": case "no": case "off":
                    return MetalValidationMode.Off;
                case "1": case "true": case "yes": case "on":
                    return MetalValidationMode.On;
                case "shaders": case "shader":
                    return MetalValidationMode.Shaders;
                default:
                    unrecognizedValue = envValue;
                    return MetalValidationMode.Off;
            }
        }

        /// <summary>
        /// Whether a process-level variable's value ARMS its tier. Metal treats the variable as a flag, so
        /// anything that is not an explicit off value counts, and an empty value counts as unset: a shell that
        /// exports the name with no value did not ask for validation.
        /// </summary>
        internal static bool IsArmed(string? processValue)
        {
            if (string.IsNullOrWhiteSpace(processValue)) return false;

            return processValue.Trim().ToLowerInvariant() switch
            {
                "0" or "false" or "no" or "off" => false,
                _ => true,
            };
        }

        /// <summary>
        /// THE CONTROL ROW 1 MEASURED, reused as a runtime check: a device created under Metal API validation is
        /// an <c>MTLDebugDevice</c> rather than the driver's own class (<c>AGXG14CDevice</c> on Apple silicon).
        /// So the engine can say what the runtime ACTUALLY did instead of repeating what the environment implied,
        /// which is the difference between a report and an echo.
        /// </summary>
        internal static bool LooksLikeADebugDevice(string deviceClassName)
            => deviceClassName is { Length: > 0 }
                && deviceClassName.Contains("Debug", StringComparison.Ordinal);

        /// <summary>The WARN body for a value that was set and understood as nothing. It lists every value that
        /// works, because the whole cost of a typo here is a diagnostic run that produced no diagnostics.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized value. Use 0 (the default, no validation), 1 (Metal "
                + "API validation) or shaders (1, plus in-shader bounds checking). This run reports whatever the "
                + "process environment already armed, which for an unrecognized value is usually nothing.";

        /// <summary>The INFO line for a run WITH validation armed, so a capture proves the lever was set rather
        /// than resting on the tester believing they set it. A run on the default says nothing, because a line on
        /// every session is a line nobody reads.</summary>
        internal static string ActiveDescription(MetalValidationMode armed, string deviceClassName) => armed switch
        {
            MetalValidationMode.On =>
                $"Metal API validation is ACTIVE for this device (from {DebugLayerVar} in the process "
                + $"environment). The device class is {deviceClassName}. Encoder-state, argument-range and "
                + "pipeline-compatibility errors are reported by the runtime. This is NOT a synchronisation "
                + "validator. Expect a performance cost.",
            MetalValidationMode.Shaders =>
                $"Metal API validation is ACTIVE with SHADER VALIDATION for this device (from {DebugLayerVar} "
                + $"and {ShaderValidationVar} in the process environment). The device class is {deviceClassName}. "
                + "Adds in-shader bounds checking on top of the API validation above. Expect a large performance "
                + "cost.",
            _ => string.Empty,
        };

        /// <summary>
        /// THE WARN THAT MATTERS MOST: the tester asked for a tier and the process cannot have it, because Metal
        /// reads its variables at launch. It names the exact prefix to re-run with, because the alternative is a
        /// session that looks validated in the log and validated nothing.
        /// </summary>
        internal static string NotArmedWarning(MetalValidationMode requested)
        {
            string vars = requested == MetalValidationMode.Shaders
                ? $"{DebugLayerVar}=1 {ShaderValidationVar}=1"
                : $"{DebugLayerVar}=1";

            return $"{EnvVarName} asked for Metal validation ({requested}) and this process was not launched with "
                + $"it. Metal reads {DebugLayerVar} and {ShaderValidationVar} from the environment BEFORE the "
                + "first device exists and offers no way to arm validation afterwards, which was measured with a "
                + "control rather than assumed. This run carries NO validation. Re-run with the variables in the "
                + $"launch environment instead: {vars} <your command>.";
        }

        /// <summary>
        /// The WARN for the one case that looks like it should work and cannot: the variable IS set, but only in
        /// this process's managed environment, which means something called
        /// <c>Environment.SetEnvironmentVariable</c> after launch. The Metal runtime never saw it. Worth its own
        /// sentence rather than folding into the one above, because the reader is looking at a variable that is
        /// demonstrably set and needs to be told why it did not count.
        /// </summary>
        internal static string SetInProcessWarning(string variableName)
            => $"{variableName} is set in this process but was NOT in the environment at launch, so the Metal "
                + "runtime never read it and this run carries no validation from it. Setting it in-process has no "
                + "effect at all, measured with a control: a device created after an in-process set is the "
                + "driver's own class, while the same run launched with the variable set is an MTLDebugDevice.";

        /// <summary>
        /// The WARN for the last disagreement: the environment armed a tier and the device came back as the
        /// driver's own class anyway. That should not happen, and if it does the report is wrong rather than the
        /// run, so it says which of the two to believe.
        /// </summary>
        internal static string ArmedButNotADebugDeviceWarning(string deviceClassName)
            => $"{DebugLayerVar} is armed in this process's launch environment and the Metal device came back as "
                + $"{deviceClassName}, which is the driver's own class rather than MTLDebugDevice. Believe the "
                + "device: this run is probably NOT validated, whatever the environment says. That combination "
                + "has not been observed, so it is worth reporting with the macOS version attached.";
    }
}
