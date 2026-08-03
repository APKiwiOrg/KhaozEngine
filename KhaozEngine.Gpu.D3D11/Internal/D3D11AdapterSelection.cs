using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>What <c>KE_D3D11_ADAPTER</c> was understood to be asking for.</summary>
    internal enum D3D11AdapterRequestKind
    {
        /// <summary>Unset or blank: let DXGI enumerate and pick, which is what the engine has always done.</summary>
        Default = 0,

        /// <summary>The software rasterizer, whatever hardware is present. The value CI pins.</summary>
        Warp = 1,

        /// <summary>The first adapter that is not flagged as software.</summary>
        Hardware = 2,

        /// <summary>A zero-based index into the DXGI enumeration order.</summary>
        Index = 3,

        /// <summary>A case-insensitive substring of an adapter description.</summary>
        NameSubstring = 4,
    }

    /// <summary>What <see cref="D3D11AdapterSelection.Choose"/> decided, which is one of three shapes rather than
    /// an index plus sentinels, so a caller cannot read "no adapter" as adapter zero.</summary>
    internal enum D3D11AdapterChoiceKind
    {
        /// <summary>Create with a null adapter and <c>DriverType.Hardware</c>, letting DXGI pick. Both the
        /// unset case and every request that could not be honoured land here.</summary>
        DefaultEnumeration = 0,

        /// <summary>Create with a null adapter and <c>DriverType.Warp</c>.</summary>
        WarpDriver = 1,

        /// <summary>Create against the enumerated adapter at <see cref="D3D11AdapterChoice.Index"/>, with
        /// <c>DriverType.Unknown</c>, which is what Direct3D requires when an adapter is supplied.</summary>
        Enumerated = 2,
    }

    /// <summary>One enumerated adapter as the selection policy needs to see it: its description and whether DXGI
    /// flagged it software. Deliberately not an <c>IDXGIAdapter</c>, so the choice is decidable from a list a test
    /// writes by hand.</summary>
    internal readonly struct D3D11AdapterInfo
    {
        internal D3D11AdapterInfo(string description, bool isSoftware)
        {
            Description = description ?? string.Empty;
            IsSoftware = isSoftware;
        }

        /// <summary>The adapter description, already through <see cref="D3D11CapabilityRead.TrimAdapterName"/>.</summary>
        internal string Description { get; }

        /// <summary><c>DXGI_ADAPTER_FLAG_SOFTWARE</c>. True for WARP and for the Microsoft Basic Render Driver.</summary>
        internal bool IsSoftware { get; }
    }

    /// <summary>The parsed request, keeping the raw value so a warning can quote what was actually typed.</summary>
    internal readonly struct D3D11AdapterRequest
    {
        internal D3D11AdapterRequest(D3D11AdapterRequestKind kind, int index, string name, string? rawValue)
        {
            Kind = kind;
            Index = index;
            Name = name ?? string.Empty;
            RawValue = rawValue;
        }

        internal D3D11AdapterRequestKind Kind { get; }

        /// <summary>The requested index, meaningful only for <see cref="D3D11AdapterRequestKind.Index"/>.</summary>
        internal int Index { get; }

        /// <summary>The requested substring, meaningful only for
        /// <see cref="D3D11AdapterRequestKind.NameSubstring"/>. Trimmed, matched case-insensitively.</summary>
        internal string Name { get; }

        /// <summary>The environment value exactly as it was read, or null when nothing was set. Verbatim on
        /// purpose: a stray quote or a trailing space is what a warning has to make visible.</summary>
        internal string? RawValue { get; }
    }

    /// <summary>The decision, and the adapter it names when there is one.</summary>
    internal readonly struct D3D11AdapterChoice
    {
        internal D3D11AdapterChoice(D3D11AdapterChoiceKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        internal D3D11AdapterChoiceKind Kind { get; }

        /// <summary>The enumeration index, meaningful only for
        /// <see cref="D3D11AdapterChoiceKind.Enumerated"/>. Negative otherwise.</summary>
        internal int Index { get; }

        internal static D3D11AdapterChoice Default => new(D3D11AdapterChoiceKind.DefaultEnumeration, -1);
        internal static D3D11AdapterChoice Warp => new(D3D11AdapterChoiceKind.WarpDriver, -1);
    }

    /// <summary>
    /// DECISION G2: <c>KE_D3D11_ADAPTER</c>, and the policy that turns it into one adapter.
    /// <para>
    /// THE PROBLEM IT SOLVES IS A CI-INTEGRITY ONE RATHER THAN A FEATURE REQUEST. Nothing in the engine selects
    /// WARP today. The Windows golden leg gets it only because <c>windows-latest</c> carries no hardware adapter
    /// and DXGI falls back, so the rasterizer the 36 committed Direct3D 11 goldens are compared on is an accident
    /// of the runner image. A runner that grows a paravirtual adapter silently changes it, and the failure would
    /// arrive as a diff on unrelated goldens with nothing anywhere naming the cause. Pinning
    /// <c>KE_D3D11_ADAPTER=warp</c> in CI turns that accident into a statement.
    /// </para>
    /// <para>
    /// A REQUEST THAT CANNOT BE HONOURED WARNS AND FALLS BACK, never fails. That is the same shape every other
    /// lever in this package has (<see cref="D3D11RecordModes"/>, <see cref="D3D11ShaderDebug"/>,
    /// <c>GpuD3D11DeviceFlags</c>), and here it matters more than usual: a name substring is machine-specific by
    /// nature, so a value that is correct on the machine it was written on is wrong on the next one, and turning
    /// that into a refusal to start would make a diagnostic lever into a way of bricking a session. The warning
    /// names what was typed AND lists what was actually present, because "no adapter matched" without the list is
    /// a message that sends the reader to look somewhere else.
    /// </para>
    /// <para>
    /// EVERYTHING HERE IS PURE EXCEPT <see cref="FromEnvironment"/>, and the enumeration it decides over is a
    /// plain list of descriptions and flags, so the whole policy runs under <c>dotnet test</c> on macOS. Only the
    /// enumeration itself is Windows-only (<c>D3D11DxgiQueries.DescribeAdaptersWindows</c>).
    /// </para>
    /// </summary>
    internal static class D3D11AdapterSelection
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Recognized values:
        /// <c>warp</c>, <c>hardware</c>, a zero-based index, or any other text as a case-insensitive substring of
        /// an adapter description. Unset or blank leaves DXGI to pick. Case-insensitive, whitespace trimmed.
        /// </summary>
        internal const string EnvVarName = "KE_D3D11_ADAPTER";

        /// <summary>
        /// What <paramref name="envValue"/> asks for. There is deliberately NO unrecognized case here: anything
        /// that is not <c>warp</c>, <c>hardware</c> or an integer is a name substring, because that is the only
        /// reading under which a user typing their GPU's name gets what they meant. Whether the request can be
        /// SATISFIED is <see cref="Choose"/>'s question, and that is where the warning lives.
        /// </summary>
        internal static D3D11AdapterRequest Parse(string? envValue)
        {
            if (string.IsNullOrWhiteSpace(envValue))
                return new D3D11AdapterRequest(D3D11AdapterRequestKind.Default, -1, string.Empty, null);

            string trimmed = envValue.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "warp":
                    return new D3D11AdapterRequest(D3D11AdapterRequestKind.Warp, -1, string.Empty, envValue);
                case "hardware":
                    return new D3D11AdapterRequest(D3D11AdapterRequestKind.Hardware, -1, string.Empty, envValue);
            }

            // Invariant culture and NumberStyles.Integer, so a machine with a comma decimal separator reads "2"
            // the same way every other machine does. A negative or oversized index parses fine here and is
            // reported by Choose against the real enumeration, which is the only place that knows the range.
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return new D3D11AdapterRequest(D3D11AdapterRequestKind.Index, index, string.Empty, envValue);

            return new D3D11AdapterRequest(D3D11AdapterRequestKind.NameSubstring, -1, trimmed, envValue);
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static D3D11AdapterRequest FromEnvironment()
            => Parse(Environment.GetEnvironmentVariable(EnvVarName));

        /// <summary>
        /// Which adapter <paramref name="request"/> names in <paramref name="adapters"/>, with
        /// <paramref name="warning"/> set when the request could not be honoured and the default enumeration is
        /// being used instead. Never throws for a bad request, by design.
        /// </summary>
        internal static D3D11AdapterChoice Choose(in D3D11AdapterRequest request,
            IReadOnlyList<D3D11AdapterInfo> adapters, out string? warning)
        {
            ArgumentNullException.ThrowIfNull(adapters);
            warning = null;

            switch (request.Kind)
            {
                case D3D11AdapterRequestKind.Default:
                    return D3D11AdapterChoice.Default;

                case D3D11AdapterRequestKind.Warp:
                    // Not resolved against the list on purpose. WARP is reachable through DriverType.Warp on every
                    // Windows machine, including one whose factory enumerates no software adapter at all, so
                    // resolving it through the enumeration would make the one value CI depends on the one value
                    // that can fail to resolve.
                    return D3D11AdapterChoice.Warp;

                case D3D11AdapterRequestKind.Hardware:
                    for (int i = 0; i < adapters.Count; i++)
                    {
                        if (!adapters[i].IsSoftware) return new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, i);
                    }
                    warning = NoHardwareWarning(request.RawValue, adapters);
                    return D3D11AdapterChoice.Default;

                case D3D11AdapterRequestKind.Index:
                    if (request.Index >= 0 && request.Index < adapters.Count)
                        return new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, request.Index);
                    warning = IndexOutOfRangeWarning(request.RawValue, request.Index, adapters);
                    return D3D11AdapterChoice.Default;

                case D3D11AdapterRequestKind.NameSubstring:
                    for (int i = 0; i < adapters.Count; i++)
                    {
                        if (adapters[i].Description.Contains(request.Name, StringComparison.OrdinalIgnoreCase))
                            return new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, i);
                    }
                    warning = NoNameMatchWarning(request.RawValue, request.Name, adapters);
                    return D3D11AdapterChoice.Default;

                default:
                    // Every member above is spelled out, so a member appended to D3D11AdapterRequestKind without a
                    // decision here reports itself rather than quietly reading as the default.
                    warning = $"{EnvVarName} produced an adapter request this build does not understand "
                        + $"(kind {(int)request.Kind}). Letting DXGI pick the adapter.";
                    return D3D11AdapterChoice.Default;
            }
        }

        /// <summary>
        /// Whether the session is running on a software rasterizer, as far as the CHOICE can tell. True for
        /// <see cref="D3D11AdapterChoiceKind.WarpDriver"/> by definition and for an enumerated adapter DXGI
        /// flagged software.
        /// <para>
        /// It answers false for <see cref="D3D11AdapterChoiceKind.DefaultEnumeration"/>, which is NOT a claim that
        /// the adapter is hardware: nothing here knows which adapter DXGI picked. The device reads the truth off
        /// the created device instead (<c>D3D11DxgiQueries.IsSoftwareAdapterWindows</c>), and this exists for the
        /// two cases that are decided before any device exists.
        /// </para>
        /// </summary>
        internal static bool IsSoftwareChoice(in D3D11AdapterChoice choice, IReadOnlyList<D3D11AdapterInfo> adapters)
        {
            ArgumentNullException.ThrowIfNull(adapters);

            if (choice.Kind == D3D11AdapterChoiceKind.WarpDriver) return true;
            if (choice.Kind != D3D11AdapterChoiceKind.Enumerated) return false;
            return choice.Index >= 0 && choice.Index < adapters.Count && adapters[choice.Index].IsSoftware;
        }

        /// <summary>
        /// The INFO line naming which adapter the session ran on and why, logged through the existing
        /// <c>GPU adapter:</c> line's neighbourhood rather than replacing it. The default case says nothing about
        /// the variable, because a line about an unset lever on every Windows session is a line nobody reads.
        /// </summary>
        internal static string Describe(in D3D11AdapterChoice choice, IReadOnlyList<D3D11AdapterInfo> adapters)
        {
            ArgumentNullException.ThrowIfNull(adapters);

            switch (choice.Kind)
            {
                case D3D11AdapterChoiceKind.WarpDriver:
                    return $"D3D11 adapter selection: WARP, the software rasterizer, from {EnvVarName}=warp. This "
                        + "is the rasterizer the committed Direct3D 11 goldens are baked on.";
                case D3D11AdapterChoiceKind.Enumerated:
                    string name = choice.Index >= 0 && choice.Index < adapters.Count
                        ? adapters[choice.Index].Description
                        : "an adapter that is no longer enumerated";
                    return $"D3D11 adapter selection: adapter {choice.Index} ('{name}'), from {EnvVarName}.";
                default:
                    return "D3D11 adapter selection: DXGI's own choice, which is the default. Set "
                        + $"{EnvVarName}=warp|hardware|<index>|<name substring> to pin one.";
            }
        }

        static string NoHardwareWarning(string? raw, IReadOnlyList<D3D11AdapterInfo> adapters)
            => $"{EnvVarName}='{raw}' asked for a hardware adapter and this machine enumerates none "
                + $"({DescribeList(adapters)}). Letting DXGI pick, which on a machine with no hardware adapter is "
                + "the software rasterizer.";

        static string IndexOutOfRangeWarning(string? raw, int index, IReadOnlyList<D3D11AdapterInfo> adapters)
            => $"{EnvVarName}='{raw}' asked for adapter index {index}, and this machine enumerates "
                + $"{adapters.Count} ({DescribeList(adapters)}). Letting DXGI pick.";

        static string NoNameMatchWarning(string? raw, string name, IReadOnlyList<D3D11AdapterInfo> adapters)
            => $"{EnvVarName}='{raw}' matched no adapter description containing '{name}' "
                + $"({DescribeList(adapters)}). Letting DXGI pick.";

        // The list is part of every warning above, because "nothing matched" without it is a message that sends
        // the reader to check their spelling when the real answer is usually that the machine changed.
        static string DescribeList(IReadOnlyList<D3D11AdapterInfo> adapters)
        {
            if (adapters.Count == 0) return "this machine enumerates no adapters at all";

            var sb = new System.Text.StringBuilder("enumerated: ");
            for (int i = 0; i < adapters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i).Append('=').Append('\'').Append(adapters[i].Description).Append('\'');
                if (adapters[i].IsSoftware) sb.Append(" (software)");
            }
            return sb.ToString();
        }
    }
}
