using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>What <c>KE_METAL_DEVICE</c> was understood to be asking for (decision M-N1).</summary>
    internal enum MetalDeviceRequestKind
    {
        /// <summary>Unset or blank: <c>MTLCreateSystemDefaultDevice()</c>, which is what the incumbent did and
        /// what keeps <c>GpuCapabilities.DeviceName</c> parity satisfiable by construction. On this path the
        /// machine is never enumerated at all.</summary>
        Default = 0,

        /// <summary>A zero-based index into <c>MTLCopyAllDevices()</c>.</summary>
        Index = 1,

        /// <summary>A case-insensitive substring of a device name.</summary>
        NameSubstring = 2,

        /// <summary>The first device that is NOT low-power. Metal has no discrete flag, so this is the negation
        /// of the one flag it does have.</summary>
        Discrete = 3,

        /// <summary>The first low-power device. On a dual-GPU Intel Mac that is the integrated GPU.</summary>
        Integrated = 4,

        /// <summary>The first low-power device, asked for by the name Metal itself uses. The same predicate as
        /// <see cref="Integrated"/>, kept a separate kind so a warning quotes the word the tester typed.</summary>
        LowPower = 5,
    }

    /// <summary>
    /// One enumerated <c>MTLDevice</c> as the selection policy needs to see it, with no Objective-C handle in it,
    /// so the choice is decidable from a list a test writes by hand.
    /// </summary>
    /// <param name="Name">The device's own <c>-name</c>, matched by
    /// <see cref="MetalDeviceRequestKind.NameSubstring"/> and quoted in every warning.</param>
    /// <param name="IsLowPower"><c>-isLowPower</c>. THE ONLY CLASSIFICATION METAL OFFERS: there is no
    /// <c>isDiscrete</c> and no device-type enumeration of the kind Vulkan has, so <c>discrete</c> is defined as
    /// the negation of this and the design says so rather than inventing a richer taxonomy the API cannot
    /// support.</param>
    /// <param name="IsRemovable"><c>-isRemovable</c>: an external GPU. Reported in the log line and never
    /// selected on, because a removable device vanishing mid-session is a device loss the latch reports rather
    /// than a selection question.</param>
    /// <param name="IsHeadless"><c>-isHeadless</c>: driving no display. Reported for the same reason.</param>
    /// <param name="RegistryId"><c>-registryID</c>, the only stable identity a Metal device has. Two identical
    /// cards report the same name, so a substitution line quoting names alone could not say which was taken.</param>
    /// <param name="MeetsRequirements">Whether <see cref="MetalDeviceRequirements"/> accepted it. An INELIGIBLE
    /// device is never chosen, whatever the variable asks for: honouring a pin onto a device that cannot run the
    /// backend would turn a diagnostic lever into a crash on frame one.</param>
    /// <param name="RejectionReason">Why it was rejected, or null when it was not. Quoted in the failure a
    /// machine with no eligible device produces, because on a two-device machine the interesting information is
    /// usually why the OTHER one was turned away.</param>
    internal readonly record struct MetalDeviceCandidate(
        string Name,
        bool IsLowPower,
        bool IsRemovable,
        bool IsHeadless,
        ulong RegistryId,
        bool MeetsRequirements,
        string? RejectionReason);

    /// <summary>The parsed request, keeping the raw value so a warning can quote what was actually typed.</summary>
    /// <param name="Kind">What form the value took.</param>
    /// <param name="Index">The requested index, meaningful only for
    /// <see cref="MetalDeviceRequestKind.Index"/>.</param>
    /// <param name="Name">The requested substring, meaningful only for
    /// <see cref="MetalDeviceRequestKind.NameSubstring"/>. Trimmed, matched case-insensitively.</param>
    /// <param name="RawValue">The environment value exactly as it was read, or null when nothing was set.
    /// Verbatim on purpose: a stray quote or a trailing space is what a warning has to make visible.</param>
    internal readonly record struct MetalDeviceRequest(
        MetalDeviceRequestKind Kind,
        int Index,
        string Name,
        string? RawValue);

    /// <summary>
    /// DECISION M-N1: <c>KE_METAL_DEVICE</c>, and the policy that turns it into one device.
    /// <para>
    /// THE DEFAULT IS <c>MTLCreateSystemDefaultDevice()</c> AND NOT "element zero of the enumeration", which is a
    /// decision rather than a shortcut and is the one place this differs in SHAPE from its Vulkan sibling. The
    /// incumbent Veldrid Metal backend called that function, section 14 demands ZERO capability differences, and
    /// <c>GpuCapabilities.DeviceName</c> is one of the compared members, so asking the same function for the same
    /// device is what makes parity satisfiable by construction instead of by luck. A machine where the system
    /// default is not <c>MTLCopyAllDevices()[0]</c> would otherwise silently swap the GPU underneath the one gate
    /// that has to isolate the backend swap. Preferring a discrete GPU by default is a follow-up with its own
    /// change note, exactly as phase 3's 2.9 ruled.
    /// </para>
    /// <para>
    /// SO AN ORDINARY RUN NEVER ENUMERATES. <c>MTLCopyAllDevices()</c> is reached only when the variable is set,
    /// which is also why the substitution reporting below is written against the enumerated path alone.
    /// </para>
    /// <para>
    /// CI PINS NOTHING HERE (M-G2), and that is a deliberate difference from both other backends. Both Windows
    /// legs pin <c>KE_D3D11_ADAPTER=warp</c> and both Linux legs pin <c>KE_VULKAN_DEVICE=llvmpipe</c> because each
    /// guards AGAINST an accident: a paravirtual adapter appearing, an ICD manifest moving. A hosted
    /// <c>macos-26</c> runner has exactly one device and no accident available, so a pin could only produce false
    /// failures. The integrity hole those pins close is closed here by the workflow pinning <c>macos-26</c> by
    /// number rather than to <c>macos-latest</c>.
    /// </para>
    /// <para>
    /// A REQUEST THAT CANNOT BE HONOURED WARNS AND FALLS BACK, never fails, which is the shape every lever in
    /// this fleet has. A name substring is machine-specific by nature, so a value that is right on the machine it
    /// was written on is wrong on the next one, and turning that into a refusal to start would make a diagnostic
    /// lever into a way of bricking a session. Every warning lists what was actually enumerated, because "nothing
    /// matched" without the list sends the reader to check their spelling when the real answer is usually that
    /// the machine changed.
    /// </para>
    /// <para>
    /// EVERYTHING HERE IS PURE EXCEPT <see cref="FromEnvironment"/>, and it decides over a plain list, so the
    /// whole policy runs under <c>dotnet test</c> on Linux and Windows where there is no Metal at all.
    /// </para>
    /// </summary>
    internal static class MetalDeviceSelection
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values:
        /// <c>discrete</c>, <c>integrated</c>, <c>low-power</c>, a zero-based index, or any other text as a
        /// case-insensitive substring of a device name. Unset takes the system default device.</summary>
        internal const string EnvVarName = "KE_METAL_DEVICE";

        /// <summary>What no device at all looks like, so a caller cannot read "nothing qualified" as device
        /// zero.</summary>
        internal const int NoDevice = -1;

        /// <summary>
        /// What <paramref name="envValue"/> asks for. There is deliberately no unrecognized case: anything that
        /// is not one of the three names or an integer is a name substring, because that is the only reading
        /// under which somebody typing their GPU's name gets what they meant. Whether the request can be
        /// SATISFIED is <see cref="Choose"/>'s question, and that is where the warning lives.
        /// </summary>
        internal static MetalDeviceRequest Parse(string? envValue)
        {
            if (string.IsNullOrWhiteSpace(envValue))
                return new MetalDeviceRequest(MetalDeviceRequestKind.Default, NoDevice, string.Empty, null);

            string trimmed = envValue.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "discrete":
                    return new MetalDeviceRequest(MetalDeviceRequestKind.Discrete, NoDevice, string.Empty, envValue);
                case "integrated":
                    return new MetalDeviceRequest(MetalDeviceRequestKind.Integrated, NoDevice, string.Empty, envValue);
                // Both spellings, because the design names the token with a hyphen and a tester who types the
                // property's own spelling should not get a name-substring search for "lowpower".
                case "low-power":
                case "lowpower":
                    return new MetalDeviceRequest(MetalDeviceRequestKind.LowPower, NoDevice, string.Empty, envValue);
                default:
                    break;
            }

            // Invariant culture and NumberStyles.Integer, so a machine with a comma decimal separator reads "2"
            // the same way every other machine does. A negative or oversized index parses fine here and is
            // reported by Choose against the real enumeration, which is the only place that knows the range.
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return new MetalDeviceRequest(MetalDeviceRequestKind.Index, index, string.Empty, envValue);

            return new MetalDeviceRequest(MetalDeviceRequestKind.NameSubstring, NoDevice, trimmed, envValue);
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static MetalDeviceRequest FromEnvironment()
            => Parse(Environment.GetEnvironmentVariable(EnvVarName));

        /// <summary>
        /// Which device <paramref name="request"/> names in <paramref name="devices"/>, or
        /// <see cref="NoDevice"/> when nothing in the list meets the requirements at all.
        /// <paramref name="warning"/> is set when the request could not be honoured and the first eligible device
        /// is being used instead. Never throws for a bad request, by design.
        /// <para>
        /// Only ever called on the ENUMERATED path, because <see cref="MetalDeviceRequestKind.Default"/> never
        /// gets this far: the default is a different function call rather than an index into this list.
        /// </para>
        /// </summary>
        internal static int Choose(in MetalDeviceRequest request, IReadOnlyList<MetalDeviceCandidate> devices,
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
                case MetalDeviceRequestKind.Default:
                    return fallback;

                case MetalDeviceRequestKind.Index:
                    if (request.Index >= 0 && request.Index < devices.Count
                        && devices[request.Index].MeetsRequirements)
                    {
                        return request.Index;
                    }
                    warning = IndexWarning(request, devices);
                    return fallback;

                case MetalDeviceRequestKind.NameSubstring:
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

                case MetalDeviceRequestKind.Discrete:
                    return ChooseByPower(request, devices, wantLowPower: false, "a device that is not low-power "
                        + "(Metal has no discrete flag, so that is what discrete means here)", fallback,
                        out warning);

                case MetalDeviceRequestKind.Integrated:
                case MetalDeviceRequestKind.LowPower:
                    return ChooseByPower(request, devices, wantLowPower: true, "a low-power device", fallback,
                        out warning);

                default:
                    // Every member above is spelled out, so a member appended to MetalDeviceRequestKind without a
                    // decision here reports itself rather than quietly reading as the default.
                    warning = $"{EnvVarName} produced a device request this build does not understand (kind "
                        + $"{(int)request.Kind}). Taking the first device that meets the requirements.";
                    return fallback;
            }
        }

        /// <summary>
        /// The INFO line naming which device the session ran on and why, for the ENUMERATED path. When the
        /// request was honoured this is a SELECTION, and when it was not it is a SUBSTITUTION and says so in as
        /// many words.
        /// <para>
        /// That distinction is the whole reason M-N1 asks for the line. A soak session comparing this backend
        /// against the incumbent had to be able to tell "this run chose device 1" from "device 0 could not run
        /// the backend and device 1 was substituted", because those are different machines from the
        /// measurement's point of view and only one of them is comparable with an incumbent run.
        /// </para>
        /// </summary>
        internal static string Describe(int chosen, in MetalDeviceRequest request,
            IReadOnlyList<MetalDeviceCandidate> devices, bool requestHonoured)
        {
            ArgumentNullException.ThrowIfNull(devices);
            if (chosen < 0 || chosen >= devices.Count)
                return "Metal device selection: no device on this machine meets the requirements.";

            MetalDeviceCandidate device = devices[chosen];
            string traits = Traits(device);

            if (requestHonoured)
            {
                return $"Metal device selection: device {chosen} ('{device.Name}'{traits}, registry ID "
                    + device.RegistryId.ToString(CultureInfo.InvariantCulture) + "), from "
                    + $"{EnvVarName}='{request.RawValue}'. This is a SELECTION rather than the system default, so "
                    + "a capability comparison against the incumbent Metal backend is only meaningful if that run "
                    + "was pinned the same way.";
            }

            return $"Metal device selection: device {chosen} ('{device.Name}'{traits}, registry ID "
                + device.RegistryId.ToString(CultureInfo.InvariantCulture) + ") SUBSTITUTED, because "
                + $"{EnvVarName}='{request.RawValue}' could not be honoured on this machine. This is a "
                + "substitution and not a selection, so a measurement taken here is not comparable with one taken "
                + "on the requested device.";
        }

        /// <summary>The INFO line for the DEFAULT path, where nothing was enumerated and there is nothing to
        /// substitute. It names the variable, because a session log that never mentions the lever is a session
        /// log in which nobody discovers it exists.</summary>
        internal static string DescribeSystemDefault(string deviceName)
            => $"Metal device selection: the system default device ('{deviceName}'), which is what the incumbent "
                + $"Metal backend uses too. Set {EnvVarName}=discrete|integrated|low-power|<index>|<name "
                + "substring> to pin a different one.";

        // The fallback every unhonourable request lands on: the FIRST device that meets the requirements.
        static int FirstEligible(IReadOnlyList<MetalDeviceCandidate> devices)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].MeetsRequirements) return i;
            }
            return NoDevice;
        }

        static int ChooseByPower(in MetalDeviceRequest request, IReadOnlyList<MetalDeviceCandidate> devices,
            bool wantLowPower, string described, int fallback, out string? warning)
        {
            warning = null;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].MeetsRequirements && devices[i].IsLowPower == wantLowPower) return i;
            }
            warning = NoMatchWarning(request, described, devices);
            return fallback;
        }

        static string IndexWarning(in MetalDeviceRequest request, IReadOnlyList<MetalDeviceCandidate> devices)
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

        static string NoMatchWarning(in MetalDeviceRequest request, string described,
            IReadOnlyList<MetalDeviceCandidate> devices)
            => $"{EnvVarName}='{request.RawValue}' asked for {described} and this machine has none that can run "
                + $"the backend ({DescribeList(devices)}). Taking the first device that meets the requirements.";

        // The list is part of every warning above, for the same reason the Direct3D 11 adapter warnings carry
        // theirs: "nothing matched" without it is a message that sends the reader to check their spelling.
        static string DescribeList(IReadOnlyList<MetalDeviceCandidate> devices)
        {
            if (devices.Count == 0) return "this machine enumerates no Metal devices at all";

            var sb = new StringBuilder("enumerated: ");
            for (int i = 0; i < devices.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i).Append("='").Append(devices[i].Name).Append('\'');
                string traits = Traits(devices[i]);
                if (traits.Length > 0) sb.Append(" (").Append(traits.AsSpan(2)).Append(')');
                if (!devices[i].MeetsRequirements) sb.Append(" INELIGIBLE");
            }
            return sb.ToString();
        }

        // The three read-only flags, as a suffix that is empty when a device is an ordinary attached GPU. Written
        // once because every warning and both INFO lines want the same phrasing.
        static string Traits(in MetalDeviceCandidate device)
        {
            var sb = new StringBuilder();
            if (device.IsLowPower) sb.Append(", low-power");
            if (device.IsRemovable) sb.Append(", removable");
            if (device.IsHeadless) sb.Append(", headless");
            return sb.ToString();
        }
    }
}
