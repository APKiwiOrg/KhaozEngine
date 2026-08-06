using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>What <c>KE_VULKAN_VALIDATION</c> was understood to be asking for (decision V-G3). A LADDER rather
    /// than a flag set: each rung is everything below it plus one more thing, so a session picks a level rather
    /// than composing options that can disagree.</summary>
    internal enum VulkanValidationMode
    {
        /// <summary>Unset, blank or an off value. No layer, no messenger, no object names. The default, and the
        /// only rung with no runtime cost at all.</summary>
        Off = 0,

        /// <summary><c>VK_LAYER_KHRONOS_validation</c> plus a <c>VK_EXT_debug_utils</c> messenger pumping into the
        /// engine logger through a rate limiter, with error and warning severities promoted.</summary>
        On = 1,

        /// <summary><see cref="On"/>, and the pump LATCHES an error-severity message and throws at a controlled
        /// point. Never inside the callback: unwinding a managed exception through native driver frames is not a
        /// diagnostic (V-G5).</summary>
        Strict = 2,

        /// <summary><see cref="On"/> plus the synchronisation-validation feature, requested by chaining
        /// <c>VkValidationFeaturesEXT</c> into <c>VkInstanceCreateInfo.pNext</c>. The rung that finds a memory
        /// aliasing hazard lavapipe would otherwise render straight through.</summary>
        Sync = 3,
    }

    /// <summary>
    /// DECISION V-G3's KNOB: <c>KE_VULKAN_VALIDATION</c>, parsed, and everything the parse has to say about
    /// itself. Pure except <see cref="FromEnvironment"/>, so the whole ladder is decided under <c>dotnet test</c>
    /// on a machine with no Vulkan loader.
    /// <para>
    /// THREE DEPARTURES FROM THE INCUMBENT LIVE HERE, and all three are bug fixes rather than preferences. This
    /// asks for <c>VK_EXT_debug_utils</c> and never the <c>VK_EXT_debug_report</c> the incumbent uses, which has
    /// been deprecated for six years. It requests exactly one layer, <c>VK_LAYER_KHRONOS_validation</c>, and never
    /// the long-removed <c>VK_LAYER_LUNARG_standard_validation</c> the incumbent also asks for. And the pump this
    /// knob switches on LOGS, where the incumbent's callback throws a managed exception and calls
    /// <c>Debugger.Break()</c> from inside a native driver callback.
    /// </para>
    /// <para>
    /// AN UNRECOGNIZED VALUE WARNS AND STAYS OFF, which is the shape every other lever in this fleet has
    /// (<c>KE_D3D11_DEBUG</c>, <c>KE_D3D11_ADAPTER</c>, <c>KE_D3D11_RECORD</c>). A typo must not stop a session
    /// starting, and it must not silently read as the level above the one that was meant either, so the warning
    /// names what was typed and lists every value that works.
    /// </para>
    /// </summary>
    internal static class VulkanValidation
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values: <c>0</c>,
        /// <c>1</c>, <c>strict</c> and <c>sync</c>, plus the usual on/off spellings. Case-insensitive,
        /// whitespace trimmed.</summary>
        internal const string EnvVarName = "KE_VULKAN_VALIDATION";

        /// <summary>The one validation layer this backend ever requests. The incumbent additionally asks for
        /// <c>VK_LAYER_LUNARG_standard_validation</c>, which was removed from the SDK in 2020 and which a modern
        /// loader answers by failing instance creation outright.</summary>
        internal const string LayerName = "VK_LAYER_KHRONOS_validation";

        /// <summary>
        /// What <paramref name="envValue"/> asks for, with <paramref name="unrecognizedValue"/> set verbatim
        /// (quotes, stray spaces and all) when the value was neither blank nor understood.
        /// <para>
        /// There is deliberately NO "anything else means on" reading here, unlike
        /// <see cref="VulkanPhysicalDeviceSelection"/>'s substring arm. A device name is free text by nature so
        /// an unrecognized value there is still meaningful, while this knob has four values and a fifth is a
        /// typo. Reading a typo as a level would be the worst outcome available: a session that believes it is
        /// running <c>strict</c> and is running nothing produces a clean run that proves nothing.
        /// </para>
        /// </summary>
        internal static VulkanValidationMode Parse(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return VulkanValidationMode.Off;

            switch (envValue.Trim().ToLowerInvariant())
            {
                case "0": case "false": case "no": case "off":
                    return VulkanValidationMode.Off;
                case "1": case "true": case "yes": case "on":
                    return VulkanValidationMode.On;
                case "strict":
                    return VulkanValidationMode.Strict;
                case "sync":
                    return VulkanValidationMode.Sync;
                default:
                    unrecognizedValue = envValue;
                    return VulkanValidationMode.Off;
            }
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static VulkanValidationMode FromEnvironment(out string? unrecognizedValue)
            => Parse(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>Whether <paramref name="mode"/> wants the layer, the <c>VK_EXT_debug_utils</c> instance
        /// extension and the messenger. True for every rung above <see cref="VulkanValidationMode.Off"/>: the
        /// layer with no messenger would report into a queue nobody drains.</summary>
        internal static bool WantsMessenger(VulkanValidationMode mode) => mode != VulkanValidationMode.Off;

        /// <summary>Whether <paramref name="mode"/> chains <c>VkValidationFeaturesEXT</c> asking for
        /// synchronisation validation. The <c>sync</c> rung only, because it is a real cost even by validation's
        /// standards and because a session that wanted it asked for it by name.</summary>
        internal static bool WantsSynchronizationValidation(VulkanValidationMode mode)
            => mode == VulkanValidationMode.Sync;

        /// <summary>Whether an error-severity message should be LATCHED and thrown at a controlled point. The
        /// <c>strict</c> rung only. The throw never happens inside the callback (V-G5).</summary>
        internal static bool ThrowsOnError(VulkanValidationMode mode) => mode == VulkanValidationMode.Strict;

        /// <summary>The WARN body for a value that was set and understood as nothing. It lists every value that
        /// works, because the whole cost of a typo here is a diagnostic run that produced no diagnostics.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is not a recognized value. Use 0 (the default, no validation), 1 (the "
                + "Khronos validation layer plus a debug-utils messenger pumping into this log), strict (1, and "
                + "an error-severity message throws at a controlled point) or sync (1, plus synchronisation "
                + "validation). Validation stays OFF for this run, so it carries none of the instrumentation the "
                + "variable was set to get.";

        /// <summary>The INFO line for a run WITH validation on, so a capture proves the lever was set rather than
        /// resting on the tester believing they set it. A run on the default says nothing, because a line on
        /// every session is a line nobody reads.</summary>
        internal static string ActiveDescription(VulkanValidationMode mode) => mode switch
        {
            VulkanValidationMode.On =>
                $"Vulkan validation is ACTIVE for this device (from {EnvVarName}=1): {LayerName} plus a "
                + "VK_EXT_debug_utils messenger pumping into this log at a rate limit, with warning and error "
                + "severities promoted. Objects this backend creates are named, so a message names a buffer "
                + "instead of a handle. Expect a large performance cost.",
            VulkanValidationMode.Strict =>
                $"Vulkan validation is ACTIVE and STRICT for this device (from {EnvVarName}=strict): everything "
                + "the 1 rung does, and the first error-severity message is latched and thrown at the next "
                + "controlled point. The throw never happens inside the driver callback. Expect a large "
                + "performance cost.",
            VulkanValidationMode.Sync =>
                $"Vulkan validation is ACTIVE with SYNCHRONISATION VALIDATION for this device (from "
                + $"{EnvVarName}=sync): everything the 1 rung does, plus VkValidationFeaturesEXT asking the layer "
                + "for synchronisation validation. This is the rung that finds a memory aliasing hazard lavapipe "
                + "renders straight through. Expect a very large performance cost.",
            _ => string.Empty,
        };

        /// <summary>The WARN body for a validation request this machine cannot satisfy, naming the fix. Creation
        /// goes on WITHOUT validation rather than refusing, for the same reason the Direct3D 11 debug layer does:
        /// the person who set the variable is by definition mid-diagnosis, and stopping their app from starting
        /// is the least useful thing to do to them.</summary>
        internal static string LayerUnavailableWarning(VulkanValidationMode mode)
            => $"{EnvVarName} asked for Vulkan validation ({mode}) and this machine has no {LayerName} installed. "
                + "The instance was created WITHOUT it, so this run reports no validation messages. Install the "
                + "Vulkan validation layers (the vulkan-validationlayers package on Linux, the Vulkan SDK "
                + "elsewhere) to get them.";
    }
}
