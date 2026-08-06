using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>What <c>KE_VULKAN_DEVICE</c> was understood to be asking for (decision V-N3).</summary>
    internal enum VulkanDeviceRequestKind
    {
        /// <summary>Unset or blank: the first enumerated device that meets the requirements, which reproduces the
        /// incumbent's <c>physicalDevices[0]</c> on every machine where device zero qualifies.</summary>
        Default = 0,

        /// <summary>A zero-based index into the <c>vkEnumeratePhysicalDevices</c> order.</summary>
        Index = 1,

        /// <summary>A case-insensitive substring of a device name.</summary>
        NameSubstring = 2,

        /// <summary>The Mesa software rasterizer, by driver id or by name. The value CI pins.</summary>
        Llvmpipe = 3,

        /// <summary>The first <c>VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU</c>.</summary>
        Discrete = 4,

        /// <summary>The first <c>VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU</c>.</summary>
        Integrated = 5,

        /// <summary>The first <c>VK_PHYSICAL_DEVICE_TYPE_CPU</c>.</summary>
        Cpu = 6,
    }

    /// <summary><c>VkPhysicalDeviceType</c> as the selection policy needs to see it, with its own spelling so the
    /// policy names no Silk.NET type and runs under <c>dotnet test</c> on a machine with no loader. The same
    /// split <see cref="VulkanDeviceFacts"/> takes for the probe, for the same reason.</summary>
    internal enum VulkanPhysicalDeviceClass
    {
        /// <summary><c>VK_PHYSICAL_DEVICE_TYPE_OTHER</c>, and what an unrecognized value maps to.</summary>
        Other = 0,

        /// <summary><c>VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU</c>.</summary>
        Integrated = 1,

        /// <summary><c>VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU</c>.</summary>
        Discrete = 2,

        /// <summary><c>VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU</c>.</summary>
        Virtual = 3,

        /// <summary><c>VK_PHYSICAL_DEVICE_TYPE_CPU</c>. Half of <c>SoftwareRasterizer</c> (V-G2).</summary>
        Cpu = 4,
    }

    /// <summary>One enumerated physical device as the selection policy needs to see it: what it is called, what
    /// kind of device it is, whether it is the Mesa software rasterizer, and whether it met the requirements.
    /// Deliberately not a <c>VkPhysicalDevice</c>, so the choice is decidable from a list a test writes by
    /// hand.</summary>
    /// <param name="Name">The driver's own name for the device, matched by
    /// <see cref="VulkanDeviceRequestKind.NameSubstring"/> and quoted in every warning.</param>
    /// <param name="Class">The device type, which the <c>discrete</c>, <c>integrated</c> and <c>cpu</c> tokens
    /// select on.</param>
    /// <param name="IsLlvmpipe">Whether <c>VkPhysicalDeviceDriverProperties.driverID</c> is
    /// <c>VK_DRIVER_ID_MESA_LLVMPIPE</c>, or the name says llvmpipe on a driver too old to report one.</param>
    /// <param name="MeetsRequirements">Whether <see cref="VulkanDeviceRequirements"/> accepted it. An INELIGIBLE
    /// device is never chosen, whatever the variable asks for: honouring a pin onto a device that cannot run the
    /// backend would turn a diagnostic lever into a crash on frame one.</param>
    /// <param name="RejectionReason">Why it was rejected, or null when it was not. Quoted in the failure a
    /// machine with no eligible device produces, because on a two-device machine the interesting information is
    /// usually why the OTHER one was turned away.</param>
    internal readonly record struct VulkanPhysicalDeviceInfo(
        string Name,
        VulkanPhysicalDeviceClass Class,
        bool IsLlvmpipe,
        bool MeetsRequirements,
        string? RejectionReason)
    {
        /// <summary>Decision V-G2's telemetry read: <c>deviceType == Cpu || driverID == MesaLlvmpipe</c>, landing
        /// in the EXISTING <c>softwareAdapter</c> field rather than in a new one.</summary>
        internal bool IsSoftwareRasterizer => Class == VulkanPhysicalDeviceClass.Cpu || IsLlvmpipe;
    }

    /// <summary>The parsed request, keeping the raw value so a warning can quote what was actually typed.</summary>
    /// <param name="Kind">What form the value took.</param>
    /// <param name="Index">The requested index, meaningful only for <see cref="VulkanDeviceRequestKind.Index"/>.</param>
    /// <param name="Name">The requested substring, meaningful only for
    /// <see cref="VulkanDeviceRequestKind.NameSubstring"/>. Trimmed, matched case-insensitively.</param>
    /// <param name="RawValue">The environment value exactly as it was read, or null when nothing was set.
    /// Verbatim on purpose: a stray quote or a trailing space is what a warning has to make visible.</param>
    internal readonly record struct VulkanDeviceRequest(
        VulkanDeviceRequestKind Kind,
        int Index,
        string Name,
        string? RawValue);

    /// <summary>
    /// DECISION V-N3 and V-G2: <c>KE_VULKAN_DEVICE</c>, and the policy that turns it into one physical device.
    /// <para>
    /// THE DEFAULT REPRODUCES THE INCUMBENT'S <c>physicalDevices[0]</c>, filtered by the requirements, and that
    /// is a decision rather than a shortcut. Scoring devices was rejected in section 2.9: it changes which GPU the
    /// engine runs on for reasons unrelated to swapping the backend, it breaks <c>DeviceName</c> parity in a
    /// design demanding ZERO capability differences, and it adds a second variable to the one gate that has to
    /// isolate the swap. Preferring a discrete device by default is follow-up VF3, with its own change note.
    /// </para>
    /// <para>
    /// THE HOLE THIS CLOSES IS WORSE THAN THE DIRECT3D 11 ONE. There, <c>KE_D3D11_ADAPTER</c> guards against a
    /// runner image growing a paravirtual adapter. Here the Linux leg pins lavapipe through
    /// <c>VK_ICD_FILENAMES</c> and <c>VK_DRIVER_FILES</c>, a LOADER-level pin the workflow has already had to
    /// repair once when an image moved the ICD manifest, and the incumbent then takes device zero
    /// unconditionally. <c>KE_VULKAN_DEVICE=llvmpipe</c> is the belt to that brace, and without it a runner that
    /// enumerates anything before lavapipe silently changes the rasterizer under the golden gate.
    /// </para>
    /// <para>
    /// A REQUEST THAT CANNOT BE HONOURED WARNS AND FALLS BACK TO THE DEFAULT, never fails, which is the shape
    /// every lever in this fleet has. A name substring is machine-specific by nature, so a value that is right on
    /// the machine it was written on is wrong on the next one, and turning that into a refusal to start would
    /// make a diagnostic lever into a way of bricking a session. Every warning lists what was actually
    /// enumerated, because "nothing matched" without the list sends the reader to check their spelling when the
    /// real answer is usually that the machine changed.
    /// </para>
    /// <para>
    /// AN INELIGIBLE DEVICE IS NEVER CHOSEN, on any path including an explicit index. Everything here is pure
    /// except <see cref="FromEnvironment"/>, and it decides over a plain list, so the whole policy runs under
    /// <c>dotnet test</c> on a machine with no Vulkan loader at all.
    /// </para>
    /// </summary>
    internal static class VulkanPhysicalDeviceSelection
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values: <c>llvmpipe</c>,
        /// <c>discrete</c>, <c>integrated</c>, <c>cpu</c>, a zero-based index, or any other text as a
        /// case-insensitive substring of a device name. Unset takes the first eligible device.</summary>
        internal const string EnvVarName = "KE_VULKAN_DEVICE";

        /// <summary>What no device at all looks like, so a caller cannot read "nothing qualified" as device
        /// zero.</summary>
        internal const int NoDevice = -1;

        /// <summary>
        /// What <paramref name="envValue"/> asks for. There is deliberately no unrecognized case: anything that
        /// is not one of the four names or an integer is a name substring, because that is the only reading under
        /// which somebody typing their GPU's name gets what they meant. Whether the request can be SATISFIED is
        /// <see cref="Choose"/>'s question, and that is where the warning lives.
        /// </summary>
        internal static VulkanDeviceRequest Parse(string? envValue)
        {
            if (string.IsNullOrWhiteSpace(envValue))
                return new VulkanDeviceRequest(VulkanDeviceRequestKind.Default, NoDevice, string.Empty, null);

            string trimmed = envValue.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "llvmpipe":
                    return new VulkanDeviceRequest(VulkanDeviceRequestKind.Llvmpipe, NoDevice, string.Empty, envValue);
                case "discrete":
                    return new VulkanDeviceRequest(VulkanDeviceRequestKind.Discrete, NoDevice, string.Empty, envValue);
                case "integrated":
                    return new VulkanDeviceRequest(VulkanDeviceRequestKind.Integrated, NoDevice, string.Empty, envValue);
                case "cpu":
                    return new VulkanDeviceRequest(VulkanDeviceRequestKind.Cpu, NoDevice, string.Empty, envValue);
            }

            // Invariant culture and NumberStyles.Integer, so a machine with a comma decimal separator reads "2"
            // the same way every other machine does. A negative or oversized index parses fine here and is
            // reported by Choose against the real enumeration, which is the only place that knows the range.
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return new VulkanDeviceRequest(VulkanDeviceRequestKind.Index, index, string.Empty, envValue);

            return new VulkanDeviceRequest(VulkanDeviceRequestKind.NameSubstring, NoDevice, trimmed, envValue);
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static VulkanDeviceRequest FromEnvironment()
            => Parse(Environment.GetEnvironmentVariable(EnvVarName));

        /// <summary>
        /// Which device <paramref name="request"/> names in <paramref name="devices"/>, or
        /// <see cref="NoDevice"/> when nothing in the list meets the requirements at all.
        /// <paramref name="warning"/> is set when the request could not be honoured and the default is being used
        /// instead. Never throws for a bad request, by design.
        /// </summary>
        internal static int Choose(in VulkanDeviceRequest request, IReadOnlyList<VulkanPhysicalDeviceInfo> devices,
            out string? warning)
        {
            ArgumentNullException.ThrowIfNull(devices);
            warning = null;

            int fallback = FirstEligible(devices);
            // Nothing qualifies, so there is nothing to warn ABOUT: the caller's failure names every device and
            // its own rejection reason, which is strictly more than a warning about a variable could say.
            if (fallback == NoDevice) return NoDevice;

            switch (request.Kind)
            {
                case VulkanDeviceRequestKind.Default:
                    return fallback;

                case VulkanDeviceRequestKind.Index:
                    if (request.Index >= 0 && request.Index < devices.Count && devices[request.Index].MeetsRequirements)
                        return request.Index;
                    warning = IndexWarning(request, devices);
                    return fallback;

                case VulkanDeviceRequestKind.NameSubstring:
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (devices[i].MeetsRequirements
                            && devices[i].Name.Contains(request.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return i;
                        }
                    }
                    warning = NoMatchWarning(request, $"a device whose name contains '{request.Name}'", devices);
                    return fallback;

                case VulkanDeviceRequestKind.Llvmpipe:
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (devices[i].MeetsRequirements && devices[i].IsLlvmpipe) return i;
                    }
                    warning = NoMatchWarning(request, "the Mesa llvmpipe software rasterizer", devices);
                    return fallback;

                case VulkanDeviceRequestKind.Discrete:
                    return ChooseByClass(request, devices, VulkanPhysicalDeviceClass.Discrete, "a discrete GPU",
                        fallback, out warning);

                case VulkanDeviceRequestKind.Integrated:
                    return ChooseByClass(request, devices, VulkanPhysicalDeviceClass.Integrated,
                        "an integrated GPU", fallback, out warning);

                case VulkanDeviceRequestKind.Cpu:
                    return ChooseByClass(request, devices, VulkanPhysicalDeviceClass.Cpu, "a CPU device",
                        fallback, out warning);

                default:
                    // Every member above is spelled out, so a member appended to VulkanDeviceRequestKind without a
                    // decision here reports itself rather than quietly reading as the default.
                    warning = $"{EnvVarName} produced a device request this build does not understand (kind "
                        + $"{(int)request.Kind}). Taking the first device that meets the requirements.";
                    return fallback;
            }
        }

        /// <summary>
        /// The INFO line naming which device the session ran on and why. The DEFAULT case says which index was
        /// taken and, when that is not index zero, says so as a SUBSTITUTION in as many words.
        /// <para>
        /// That distinction is the whole reason this line exists on the default path where the D3D11 equivalent
        /// stays quiet. A soak session comparing this backend against the incumbent has to be able to tell "this
        /// run chose device 1" from "device 0 could not run the backend and device 1 was substituted", because
        /// those are different machines from the measurement's point of view and only one of them is comparable
        /// with an incumbent run that took device zero unconditionally.
        /// </para>
        /// </summary>
        internal static string Describe(int chosen, in VulkanDeviceRequest request,
            IReadOnlyList<VulkanPhysicalDeviceInfo> devices)
        {
            ArgumentNullException.ThrowIfNull(devices);
            if (chosen < 0 || chosen >= devices.Count)
                return "Vulkan device selection: no physical device on this machine meets the requirements.";

            string name = devices[chosen].Name;
            string software = devices[chosen].IsSoftwareRasterizer ? ", a SOFTWARE rasterizer" : string.Empty;

            if (request.Kind != VulkanDeviceRequestKind.Default)
            {
                return $"Vulkan device selection: device {chosen} ('{name}'{software}), from "
                    + $"{EnvVarName}='{request.RawValue}'.";
            }

            if (chosen == 0)
            {
                return $"Vulkan device selection: device 0 ('{name}'{software}), the default. Set "
                    + $"{EnvVarName}=llvmpipe|discrete|integrated|cpu|<index>|<name substring> to pin one.";
            }

            return $"Vulkan device selection: device {chosen} ('{name}'{software}) SUBSTITUTED for device 0, "
                + $"which cannot run this backend ({devices[0].RejectionReason ?? "no reason recorded"}). This is "
                + "a substitution and not a selection, so a measurement taken here is not comparable with one "
                + $"taken on device 0. Set {EnvVarName} to pin a device explicitly.";
        }

        // The default, and the fallback every unhonourable request lands on: the FIRST device that meets the
        // requirements, which is physicalDevices[0] on every machine where device zero qualifies.
        static int FirstEligible(IReadOnlyList<VulkanPhysicalDeviceInfo> devices)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].MeetsRequirements) return i;
            }
            return NoDevice;
        }

        static int ChooseByClass(in VulkanDeviceRequest request, IReadOnlyList<VulkanPhysicalDeviceInfo> devices,
            VulkanPhysicalDeviceClass wanted, string described, int fallback, out string? warning)
        {
            warning = null;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].MeetsRequirements && devices[i].Class == wanted) return i;
            }
            warning = NoMatchWarning(request, described, devices);
            return fallback;
        }

        static string IndexWarning(in VulkanDeviceRequest request, IReadOnlyList<VulkanPhysicalDeviceInfo> devices)
        {
            if (request.Index < 0 || request.Index >= devices.Count)
            {
                return $"{EnvVarName}='{request.RawValue}' asked for device index {request.Index}, and this "
                    + $"machine enumerates {devices.Count} ({DescribeList(devices)}). Taking the first device "
                    + "that meets the requirements.";
            }

            return $"{EnvVarName}='{request.RawValue}' asked for device index {request.Index}, which cannot run "
                + $"this backend ({devices[request.Index].RejectionReason ?? "no reason recorded"}). Taking the "
                + "first device that meets the requirements instead, because honouring the pin would trade a "
                + "warning now for a crash on frame one.";
        }

        static string NoMatchWarning(in VulkanDeviceRequest request, string described,
            IReadOnlyList<VulkanPhysicalDeviceInfo> devices)
            => $"{EnvVarName}='{request.RawValue}' asked for {described} and this machine has none that can run "
                + $"the backend ({DescribeList(devices)}). Taking the first device that meets the requirements.";

        // The list is part of every warning above, for the same reason the Direct3D 11 adapter warnings carry
        // theirs: "nothing matched" without it is a message that sends the reader to check their spelling.
        static string DescribeList(IReadOnlyList<VulkanPhysicalDeviceInfo> devices)
        {
            if (devices.Count == 0) return "this machine enumerates no physical devices at all";

            var sb = new StringBuilder("enumerated: ");
            for (int i = 0; i < devices.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i).Append("='").Append(devices[i].Name).Append('\'');
                sb.Append(" (").Append(devices[i].Class);
                if (devices[i].IsLlvmpipe) sb.Append(", llvmpipe");
                if (!devices[i].MeetsRequirements) sb.Append(", INELIGIBLE");
                sb.Append(')');
            }
            return sb.ToString();
        }
    }
}
