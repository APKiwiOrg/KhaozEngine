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
    /// WHICH OBJECTIVE-C CLASS THE RUNTIME HANDED BACK, which is the only honest reading of whether a run is
    /// really validated. Every member of this enum is a MEASURED class name rather than a documented one: row 1's
    /// spike measured the debug device, https://github.com/APKiwiOrg/KhaozEngine/issues/614 measured the capture
    /// device, and https://github.com/APKiwiOrg/KhaozEngine/issues/628 measured the shader-validation device on
    /// run 31874140088.
    /// </summary>
    internal enum MetalDeviceClass
    {
        /// <summary>The driver's own class (<c>AGXG14CDevice</c> on Apple silicon). Nothing is holding the
        /// device, either because nothing was armed or because something displaced what was.</summary>
        Driver = 0,

        /// <summary><c>MTLDebugDevice</c>, the API validation layer holding the device. What
        /// <c>MTL_DEBUG_LAYER=1</c> gets, with or without <c>MTL_SHADER_VALIDATION</c> beside it.</summary>
        Debug = 1,

        /// <summary>
        /// The SHADER validation layer holding the device, which is what <c>MTL_SHADER_VALIDATION=1</c> ALONE
        /// gets. A validated device rather than an unvalidated one. Reading it as unvalidated is the defect #628
        /// records: a run that really was validating emitted 99 warnings telling its reader to disbelieve it.
        /// <para>
        /// <b>IT HAS TWO MEASURED SPELLINGS, WHICH IS WHY NOTHING HERE COMPARES AGAINST ONE CLASS NAME.</b>
        /// Hosted <c>macos-26</c> on a paravirtual GPU reported <c>MTLLegacySVDevice</c> (run 31874140088, the
        /// reading #628 was filed on), and real Apple silicon reported <c>MTLGPUDebugDevice</c> for the same
        /// launch environment (Mac14,6 / Apple M2 Max, macOS 26.6.1 build 25G76). Pinning the check to either
        /// one re-creates the false warning on the other machine, so the WARN asks whether ANY validation
        /// wrapper is holding the device rather than whether one exact class came back.
        /// </para>
        /// </summary>
        ShaderValidation = 2,

        /// <summary><c>CaptureMTLDevice</c>, a GPU-trace capture holding the device. A capture DISPLACES the
        /// validation layer rather than sitting beside it (#614), so this one is genuinely not validated.</summary>
        Capture = 3,
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
    /// a validated device really is a different Objective-C class. <see cref="ClassifyDevice"/> is that check, so
    /// the log line says what the runtime actually did rather than what the environment implied.</para>
    ///
    /// <para><b>THE CHECK IS PER-VARIABLE, BECAUSE THE TWO VARIABLES GET DIFFERENT CLASSES.</b> It was a single
    /// "does the class contain Debug" test until #628, which is wrong twice over on a run armed with
    /// <c>MTL_SHADER_VALIDATION</c> alone: that run gets <see cref="MetalDeviceClass.ShaderValidation"/>, a
    /// perfectly validated device, and the warning it fired named <c>MTL_DEBUG_LAYER</c>, a variable nobody had
    /// set. So <see cref="ExpectedDeviceClass"/> says which class each arming should produce, and the WARN fires
    /// only when a variable IS armed and the class that came back is not the one it should have got, naming the
    /// variable that was actually set.</para>
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

        /// <summary>The class a device under Metal API validation reports, measured by row 1's spike.</summary>
        internal const string DebugDeviceClass = "MTLDebugDevice";

        /// <summary>One of the two classes a device under shader validation ALONE reports: what hosted
        /// <c>macos-26</c> answered on run 31874140088, which is the reading #628 was filed on.</summary>
        internal const string ShaderValidationDeviceClass = "MTLLegacySVDevice";

        /// <summary>The OTHER class shader validation alone reports, measured on real Apple silicon (M2 Max,
        /// macOS 26.6.1 build 25G76) while fixing #628. Two spellings for one tier, which is why nothing here
        /// compares a device class for equality.</summary>
        internal const string GpuDebugDeviceClass = "MTLGPUDebugDevice";

        /// <summary>The class a device under a GPU-trace capture reports, measured for #614.</summary>
        internal const string CaptureDeviceClass = "CaptureMTLDevice";

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
        /// THE CONTROL ROW 1 MEASURED, reused as a runtime check and widened to all four classes: what the
        /// runtime handed back is what is really holding the device, whatever the environment implied. Matched on
        /// a substring rather than on equality because the driver's own class carries a chip name in it and the
        /// wrappers have carried more than one spelling across macOS versions.
        /// <para>
        /// THE ORDER IS LOAD-BEARING TWICE. Capture is tested first because it displaces, so a process with both
        /// a capture and the debug layer armed gets <c>CaptureMTLDevice</c> and must read as a capture rather
        /// than as anything validated. And <c>MTLGPUDebugDevice</c> is tested before <c>MTLDebugDevice</c>,
        /// because the first contains the second: matching "Debug" first would file the shader-validation device
        /// under the API layer, which is what this classifier printed on its first real-hardware run.
        /// </para>
        /// </summary>
        internal static MetalDeviceClass ClassifyDevice(string deviceClassName)
        {
            if (string.IsNullOrEmpty(deviceClassName)) return MetalDeviceClass.Driver;
            if (deviceClassName.Contains("Capture", StringComparison.Ordinal)) return MetalDeviceClass.Capture;
            if (deviceClassName.Contains(GpuDebugDeviceClass, StringComparison.Ordinal)
                || deviceClassName.Contains("SVDevice", StringComparison.Ordinal))
            {
                return MetalDeviceClass.ShaderValidation;
            }

            if (deviceClassName.Contains("Debug", StringComparison.Ordinal)) return MetalDeviceClass.Debug;

            return MetalDeviceClass.Driver;
        }

        /// <summary>
        /// Whether a class means SOMETHING IS VALIDATING, which is the question the WARN actually asks. Both
        /// validation wrappers count and neither the capture wrapper nor the driver's own class does.
        /// <para>
        /// This is the whole reason the check is not "did the expected class come back". Shader validation has
        /// two measured spellings across two machines, so an equality check is a warning generator on whichever
        /// machine is not the one it was written against, and that is the failure #628 is.
        /// </para>
        /// </summary>
        internal static bool IsValidationDevice(MetalDeviceClass deviceClass)
            => deviceClass is MetalDeviceClass.Debug or MetalDeviceClass.ShaderValidation;

        /// <summary>
        /// WHICH CLASS THIS ARMING SHOULD HAVE PRODUCED. The debug layer wins when both variables are set, which
        /// is measured rather than assumed: <c>MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1</c> gets
        /// <c>MTLDebugDevice</c>, and the shader variable alone gets <c>MTLLegacySVDevice</c>.
        /// </summary>
        internal static MetalDeviceClass ExpectedDeviceClass(bool debugLayerArmed, bool shaderValidationArmed)
            => debugLayerArmed ? MetalDeviceClass.Debug
                : shaderValidationArmed ? MetalDeviceClass.ShaderValidation
                : MetalDeviceClass.Driver;

        /// <summary>The variables that ARE armed, named for a log line, so a run never reads back a variable
        /// nobody set. Empty when neither is.</summary>
        internal static string ArmedVariables(bool debugLayerArmed, bool shaderValidationArmed)
            => (debugLayerArmed, shaderValidationArmed) switch
            {
                (true, true) => DebugLayerVar + " and " + ShaderValidationVar,
                (true, false) => DebugLayerVar,
                (false, true) => ShaderValidationVar,
                _ => string.Empty,
            };

        /// <summary>What each class means in one noun phrase, for the two lines that report it.</summary>
        internal static string Describe(MetalDeviceClass deviceClass) => deviceClass switch
        {
            MetalDeviceClass.Debug => "the API validation layer holding the device",
            MetalDeviceClass.ShaderValidation => "the SHADER validation layer holding the device",
            MetalDeviceClass.Capture => "a GPU-trace capture holding the device, which is not a validation layer",
            _ => "the driver's own class, so nothing is holding the device",
        };

        /// <summary>
        /// THE ONE CONDITION WORTH A WARN: something was armed and NOTHING is validating. An unarmed process is
        /// not a disagreement, and neither is a validation wrapper of the other kind, which is what stops the
        /// warning firing on a run that really was validating.
        /// </summary>
        internal static bool DisagreesWithArming(
            bool debugLayerArmed, bool shaderValidationArmed, string deviceClassName)
            => (debugLayerArmed || shaderValidationArmed)
                && !IsValidationDevice(ClassifyDevice(deviceClassName));

        /// <summary>The WARN body for a value that was set and understood as nothing. It lists every value that
        /// works, because the whole cost of a typo here is a diagnostic run that produced no diagnostics.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized value. Use 0 (the default, no validation), 1 (Metal "
                + "API validation) or shaders (1, plus in-shader bounds checking). This run reports whatever the "
                + "process environment already armed, which for an unrecognized value is usually nothing.";

        /// <summary>
        /// The INFO line for a run WITH validation armed, so a capture proves the lever was set rather than
        /// resting on the tester believing they set it. A run on the default says nothing, because a line on
        /// every session is a line nobody reads.
        /// <para>
        /// IT NAMES THE VARIABLES THAT WERE ACTUALLY SET AND READS THE CLASS BACK. Both halves were wrong before
        /// #628: a shader-only run was described as coming from <c>MTL_DEBUG_LAYER</c> as well, and the class was
        /// printed with no reading of what it meant, so <c>MTLLegacySVDevice</c> sat in the line above a warning
        /// calling the same run unvalidated.
        /// </para>
        /// </summary>
        internal static string ActiveDescription(
            bool debugLayerArmed, bool shaderValidationArmed, string deviceClassName)
        {
            if (!debugLayerArmed && !shaderValidationArmed) return string.Empty;

            string armed = ArmedVariables(debugLayerArmed, shaderValidationArmed);
            string held = $"The device class is {deviceClassName}, "
                + Describe(ClassifyDevice(deviceClassName)) + ".";

            string tier = (debugLayerArmed, shaderValidationArmed) switch
            {
                (true, true) => "Metal API validation is ACTIVE with SHADER VALIDATION for this device (from "
                    + armed + " in the process environment). " + held + " Encoder-state, argument-range and "
                    + "pipeline-compatibility errors are reported by the runtime, with in-shader bounds checking "
                    + "on top. Expect a large performance cost.",
                (false, true) => "Metal SHADER VALIDATION is ACTIVE for this device (from " + armed
                    + " in the process environment). " + held + " In-shader bounds checking, WITHOUT the API "
                    + "validation tier, which is a separate variable. Expect a large performance cost.",
                _ => "Metal API validation is ACTIVE for this device (from " + armed
                    + " in the process environment). " + held + " Encoder-state, argument-range and "
                    + "pipeline-compatibility errors are reported by the runtime. Expect a performance cost.",
            };

            return tier + " Neither tier is a synchronisation validator, and Metal has none at all.";
        }

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
        /// The WARN for the last disagreement: a variable was armed and the device came back as something other
        /// than the class that arming produces. It says which of the two to believe, names ONLY the variables
        /// that were actually set, and separates the one cause already measured from the ones that are not.
        /// <para>
        /// Gate it on <see cref="DisagreesWithArming"/>. Calling it on an agreeing run would say the opposite of
        /// what happened, which is exactly the failure #628 records.
        /// </para>
        /// </summary>
        internal static string ArmedButWrongDeviceClassWarning(
            bool debugLayerArmed, bool shaderValidationArmed, string deviceClassName)
        {
            string armed = ArmedVariables(debugLayerArmed, shaderValidationArmed);
            MetalDeviceClass expected = ExpectedDeviceClass(debugLayerArmed, shaderValidationArmed);
            string expectedName = expected == MetalDeviceClass.ShaderValidation
                ? $"a shader-validation device ({ShaderValidationDeviceClass} and {GpuDebugDeviceClass} have "
                    + "both been measured for it)"
                : $"an {DebugDeviceClass}";

            string cause = ClassifyDevice(deviceClassName) == MetalDeviceClass.Capture
                ? "A GPU-trace capture DISPLACES the validation layer rather than sitting beside it, measured on "
                    + "real Metal (https://github.com/APKiwiOrg/KhaozEngine/issues/614), so MTL_CAPTURE_ENABLED "
                    + "is the thing to drop from the launch environment."
                : "That combination has not been observed, so it is worth reporting with the macOS version "
                    + "attached.";

            // "is" or "are" by the count ArmedVariables just named, because this line reads back a variable list
            // that is one name or two and a missing verb reads as a truncated message.
            string armedVerb = debugLayerArmed && shaderValidationArmed ? "are" : "is";

            return $"{armed} {armedVerb} armed in this process's launch environment, which gets {expectedName}, "
                + "and the "
                + $"Metal device came back as {deviceClassName} instead, which is "
                + Describe(ClassifyDevice(deviceClassName))
                + ". Believe the device: this run is probably NOT validated, whatever the environment says. "
                + cause;
        }
    }
}
