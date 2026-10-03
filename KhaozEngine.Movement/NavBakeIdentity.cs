using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Unicode;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Canonical identity block of a baked navigation set. Equal inputs encode to equal bytes in any
/// insertion order, and the bytes hold no architecture-dependent data. Layout: engine version as a <c>uint16</c>
/// byte count and UTF-8, the 13 <see cref="PhysicsNavBakeOptions"/> fields in declaration order, sources sorted by
/// ordinal label, then profiles sorted by ordinal name, each with its area filter and the 27 retained
/// <see cref="MoveTuning"/> fields.</summary>
/// <remarks>Adding a field to <see cref="MoveTuning"/> or <see cref="PhysicsNavBakeOptions"/> changes this layout.
/// Encode the field here and bump <see cref="FormatVersion"/>, so a bake from an older layout reports an
/// unsupported format rather than corrupt bytes. Changing the label character set, <see cref="MaxNameLength"/> or
/// <see cref="MaxProfiles"/> changes which stored identities decode as canonical, so it needs the same bump. The
/// golden fingerprint test changes only with that bump.</remarks>
internal static partial class NavBakeIdentity
{
    /// <summary>Bake container format version. Bump it whenever the identity or payload layout changes.</summary>
    internal const ushort FormatVersion = 1;

    internal const int MaxNameLength = 64;
    internal const int MaxProfiles = 256;
    internal const int DigestLength = 32;
    private const int OptionCount = 13;

    private const int SourceMinBytes = 1 + 1 + DigestLength;
    private const int ProfileMinBytes = 1 + 1 + 8 + TuningBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static string? s_engineVersion;

    /// <summary>The <see cref="PhysicsNavBakeOptions"/> fields in identity order, which is declaration order.</summary>
    internal static IReadOnlyList<string> OptionFieldNames { get; } =
    [
        nameof(PhysicsNavBakeOptions.MinX), nameof(PhysicsNavBakeOptions.MinZ),
        nameof(PhysicsNavBakeOptions.MaxX), nameof(PhysicsNavBakeOptions.MaxZ),
        nameof(PhysicsNavBakeOptions.CellSize), nameof(PhysicsNavBakeOptions.ProbeHeight),
        nameof(PhysicsNavBakeOptions.ProbeRange), nameof(PhysicsNavBakeOptions.MaxSlopeRadians),
        nameof(PhysicsNavBakeOptions.MaxCells), nameof(PhysicsNavBakeOptions.MaxLayerCells),
        nameof(PhysicsNavBakeOptions.MaxSurfacesPerColumn), nameof(PhysicsNavBakeOptions.EdgeProbeSeconds),
        nameof(PhysicsNavBakeOptions.MaxEdgeProbeSteps),
    ];

    /// <summary>The Movement assembly's informational version without build metadata.</summary>
    internal static string CurrentEngineVersion => s_engineVersion ??= ReadEngineVersion();

    /// <summary>Removes any <c>+metadata</c> suffix from an informational version.</summary>
    internal static string NormalizeEngineVersion(string informational)
    {
        ArgumentNullException.ThrowIfNull(informational);
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>Encodes the identity block of <paramref name="expected"/> for <paramref name="engineVersion"/>.</summary>
    /// <exception cref="ArgumentException">A name, label, digest, count or engine version is invalid, or a label or
    /// name repeats.</exception>
    internal static byte[] Encode(NavBakeExpectation expected, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(engineVersion);
        if (engineVersion.Length == 0)
            throw new ArgumentException("Engine version must not be empty.", nameof(engineVersion));
        byte[] engine = StrictUtf8.GetBytes(engineVersion);
        if (engine.Length > ushort.MaxValue)
            throw new ArgumentException("Engine version is too long.", nameof(engineVersion));
        PhysicsNavBakeOptions options = expected.Options
            ?? throw new ArgumentException("Expectation options are required.", nameof(expected));
        IReadOnlyList<(string Label, byte[] Digest)> sources = (expected.Sources
            ?? throw new ArgumentException("Expectation sources are required.", nameof(expected))).Snapshot();
        NavBakeProfile[] profiles = SortedProfiles(expected.Profiles, nameof(expected));
        if (sources.Count == 0)
            throw new ArgumentException("A bake needs at least one source.", nameof(expected));
        if (sources.Count > ushort.MaxValue)
            throw new ArgumentException("A bake holds at most 65535 sources.", nameof(expected));

        var writer = new NavBakeWriter();
        writer.WriteLongString(engine);
        WriteOptions(writer, options);
        writer.WriteUInt16((ushort)sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            (string label, byte[] digest) = sources[i];
            CheckName(label, nameof(expected), "Source label");
            if (digest.Length != DigestLength)
                throw new ArgumentException($"Source '{label}' digest must be 32 bytes.", nameof(expected));
            if (i > 0 && string.CompareOrdinal(sources[i - 1].Label, label) >= 0)
                throw new ArgumentException($"Source label '{label}' repeats.", nameof(expected));
            writer.WriteShortString(Encoding.ASCII.GetBytes(label));
            writer.WriteBytes(digest);
        }
        writer.WriteUInt16((ushort)profiles.Length);
        foreach (NavBakeProfile profile in profiles)
        {
            writer.WriteShortString(Encoding.ASCII.GetBytes(profile.Name));
            writer.WriteUInt32(profile.Areas.Required);
            writer.WriteUInt32(profile.Areas.Excluded);
            WriteTuning(writer, profile.Tuning);
        }
        return writer.ToArray();
    }

    /// <summary>Compares a stored identity with an expected one. Equal bytes are <see cref="NavBakeLoadStatus.Loaded"/>
    /// with an empty detail. Otherwise both are decoded and the first difference is named in the order engine,
    /// options, sources, profiles. Stored bytes that do not decode canonically are
    /// <see cref="NavBakeLoadStatus.Corrupt"/>. Never throws.</summary>
    internal static (NavBakeLoadStatus Status, string Detail) Compare(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> expected)
    {
        if (stored.SequenceEqual(expected)) return (NavBakeLoadStatus.Loaded, "");
        if (!TryDecode(stored, out Decoded? bake, out string problem))
            return (NavBakeLoadStatus.Corrupt, $"Stored identity {problem}.");
        if (!TryDecode(expected, out Decoded? want, out problem))
            return (NavBakeLoadStatus.Corrupt, $"Expected identity {problem}.");

        if (!string.Equals(bake.EngineVersion, want.EngineVersion, StringComparison.Ordinal))
            return (NavBakeLoadStatus.EngineChanged, $"Bake engine {bake.EngineVersion}, expected {want.EngineVersion}.");
        for (int i = 0; i < OptionCount; i++)
            if (bake.OptionBits[i] != want.OptionBits[i])
                return (NavBakeLoadStatus.OptionsChanged, $"Option {OptionFieldNames[i]}: bake " +
                    $"{FormatOption(i, bake.OptionBits[i])}, expected {FormatOption(i, want.OptionBits[i])}.");
        if (FirstSourceDifference(bake.Sources, want.Sources) is { } source)
            return (NavBakeLoadStatus.SourcesChanged, source);
        if (FirstProfileDifference(bake.Profiles, want.Profiles) is { } profile)
            return (NavBakeLoadStatus.ProfilesChanged, profile);
        return (NavBakeLoadStatus.Corrupt, "Stored identity differs from the expected bytes without a decoded difference.");
    }

    /// <summary>Decodes an identity block without throwing. False means the bytes are truncated, carry trailing data
    /// or are not canonical, and <paramref name="problem"/> says why.</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Decoded? identity,
        out string problem)
    {
        identity = null;
        var reader = new NavBakeReader(bytes);
        if (!reader.TryReadLongString(out ReadOnlySpan<byte> engine)) return Fail("is truncated in the engine version", out problem);
        if (engine.Length == 0 || !Utf8.IsValid(engine)) return Fail("has an invalid engine version", out problem);
        var options = new uint[OptionCount];
        for (int i = 0; i < OptionCount; i++)
            if (!reader.TryReadUInt32(out options[i])) return Fail("is truncated in the options", out problem);

        if (!reader.TryReadUInt16(out ushort sourceCount)) return Fail("is truncated before the sources", out problem);
        if (sourceCount == 0 || sourceCount > reader.Remaining / SourceMinBytes)
            return Fail($"has an impossible source count {sourceCount}", out problem);
        var sources = new (string Label, byte[] Digest)[sourceCount];
        ReadOnlySpan<byte> previous = default;
        for (int i = 0; i < sourceCount; i++)
        {
            if (!reader.TryReadShortString(out ReadOnlySpan<byte> label) || !reader.TryReadBytes(DigestLength, out ReadOnlySpan<byte> digest))
                return Fail("is truncated in the sources", out problem);
            if (!IsName(label)) return Fail("has a source label outside the label set", out problem);
            if (i > 0 && previous.SequenceCompareTo(label) >= 0)
                return Fail("has source labels out of order or repeated", out problem);
            sources[i] = (Encoding.ASCII.GetString(label), digest.ToArray());
            previous = label;
        }

        if (!reader.TryReadUInt16(out ushort profileCount)) return Fail("is truncated before the profiles", out problem);
        if (profileCount == 0 || profileCount > MaxProfiles || profileCount > reader.Remaining / ProfileMinBytes)
            return Fail($"has an impossible profile count {profileCount}", out problem);
        var profiles = new DecodedProfile[profileCount];
        for (int i = 0; i < profileCount; i++)
        {
            if (!reader.TryReadShortString(out ReadOnlySpan<byte> name) ||
                !reader.TryReadUInt32(out uint required) || !reader.TryReadUInt32(out uint excluded))
                return Fail("is truncated in the profiles", out problem);
            if (!IsName(name)) return Fail("has a profile name outside the label set", out problem);
            if (i > 0 && previous.SequenceCompareTo(name) >= 0)
                return Fail("has profile names out of order or repeated", out problem);
            string text = Encoding.ASCII.GetString(name);
            if ((required & excluded) != 0u) return Fail($"has overlapping area bits in profile '{text}'", out problem);
            if (!ReadTuning(ref reader, out MoveTuning tuning))
                return Fail($"has a truncated or non-canonical tuning in profile '{text}'", out problem);
            profiles[i] = new DecodedProfile(text, required, excluded, tuning);
            previous = name;
        }
        if (reader.Remaining != 0) return Fail($"has {reader.Remaining} trailing bytes", out problem);

        identity = new Decoded(Encoding.UTF8.GetString(engine), options, sources, profiles);
        problem = "";
        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="value"/> is 1 to 64 characters from the
    /// label set.</summary>
    internal static void CheckName(string? value, string parameter, string role)
    {
        if (value is null || value.Length is 0 or > MaxNameLength)
            throw new ArgumentException($"{role} must be 1 to {MaxNameLength} characters.", parameter);
        foreach (char c in value)
            if (!IsNameChar(c))
                throw new ArgumentException($"{role} '{value}' may use only a-z, 0-9, '.', '_', '-' and '/'.", parameter);
    }

    private static bool IsName(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > MaxNameLength) return false;
        foreach (byte b in value)
            if (!IsNameChar((char)b)) return false;
        return true;
    }

    private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/';

    private static NavBakeProfile[] SortedProfiles(IReadOnlyList<NavBakeProfile>? profiles, string parameter)
    {
        if (profiles is null) throw new ArgumentException("Expectation profiles are required.", parameter);
        if (profiles.Count is 0 or > MaxProfiles)
            throw new ArgumentException($"A bake holds 1 to {MaxProfiles} profiles.", parameter);
        var sorted = new NavBakeProfile[profiles.Count];
        for (int i = 0; i < sorted.Length; i++)
        {
            NavBakeProfile profile = profiles[i] ?? throw new ArgumentException("A profile is null.", parameter);
            CheckName(profile.Name, parameter, "Profile name");
            sorted[i] = profile;
        }
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (int i = 1; i < sorted.Length; i++)
            if (string.Equals(sorted[i - 1].Name, sorted[i].Name, StringComparison.Ordinal))
                throw new ArgumentException($"Profile name '{sorted[i].Name}' repeats.", parameter);
        return sorted;
    }

    private static void WriteOptions(NavBakeWriter writer, PhysicsNavBakeOptions options)
    {
        writer.WriteSingle(options.MinX);
        writer.WriteSingle(options.MinZ);
        writer.WriteSingle(options.MaxX);
        writer.WriteSingle(options.MaxZ);
        writer.WriteSingle(options.CellSize);
        writer.WriteSingle(options.ProbeHeight);
        writer.WriteSingle(options.ProbeRange);
        writer.WriteSingle(options.MaxSlopeRadians);
        writer.WriteInt32(options.MaxCells);
        writer.WriteInt32(options.MaxLayerCells);
        writer.WriteInt32(options.MaxSurfacesPerColumn);
        writer.WriteSingle(options.EdgeProbeSeconds);
        writer.WriteInt32(options.MaxEdgeProbeSteps);
    }

    // Indices of the int32 options, MaxCells, MaxLayerCells, MaxSurfacesPerColumn and MaxEdgeProbeSteps.
    private static string FormatOption(int index, uint bits) => index is 8 or 9 or 10 or 12
        ? ((int)bits).ToString(CultureInfo.InvariantCulture)
        : FormatFloat(bits);

    private static string FormatFloat(uint bits) =>
        $"{BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture)} (0x{bits:X8})";

    private static string? FirstSourceDifference((string Label, byte[] Digest)[] bake, (string Label, byte[] Digest)[] want)
    {
        int i = 0, j = 0;
        while (i < bake.Length || j < want.Length)
        {
            int order = i == bake.Length ? 1 : j == want.Length ? -1 : string.CompareOrdinal(bake[i].Label, want[j].Label);
            if (order < 0) return $"Source '{bake[i].Label}' is in the bake but not expected.";
            if (order > 0) return $"Source '{want[j].Label}' is expected but not in the bake.";
            if (!bake[i].Digest.AsSpan().SequenceEqual(want[j].Digest)) return $"Source '{bake[i].Label}' digest changed.";
            i++;
            j++;
        }
        return null;
    }

    private static string? FirstProfileDifference(DecodedProfile[] bake, DecodedProfile[] want)
    {
        int i = 0, j = 0;
        while (i < bake.Length || j < want.Length)
        {
            int order = i == bake.Length ? 1 : j == want.Length ? -1 : string.CompareOrdinal(bake[i].Name, want[j].Name);
            if (order < 0) return $"Profile '{bake[i].Name}' is in the bake but not expected.";
            if (order > 0) return $"Profile '{want[j].Name}' is expected but not in the bake.";
            if (FirstFieldDifference(bake[i], want[j]) is { } field) return $"Profile '{bake[i].Name}' {field}.";
            i++;
            j++;
        }
        return null;
    }

    private static string? FirstFieldDifference(DecodedProfile bake, DecodedProfile want)
    {
        if (bake.Required != want.Required)
            return $"field Required: bake 0x{bake.Required:X8}, expected 0x{want.Required:X8}";
        if (bake.Excluded != want.Excluded)
            return $"field Excluded: bake 0x{bake.Excluded:X8}, expected 0x{want.Excluded:X8}";
        return FirstTuningDifference(bake.Tuning, want.Tuning);
    }

    private static bool Fail(string reason, out string problem)
    {
        problem = reason;
        return false;
    }

    private static string ReadEngineVersion()
    {
        string? informational = typeof(NavBakeIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            throw new InvalidOperationException("KhaozEngine.Movement carries no informational version.");
        return NormalizeEngineVersion(informational);
    }

    /// <summary>A decoded canonical identity. Option values are raw <c>uint32</c> bit patterns in identity order.</summary>
    internal sealed record Decoded(string EngineVersion, uint[] OptionBits, (string Label, byte[] Digest)[] Sources,
        DecodedProfile[] Profiles);

    /// <summary>A decoded profile. Its area bits are proved disjoint before any <see cref="NavAreaFilter"/> exists.</summary>
    internal readonly record struct DecodedProfile(string Name, uint Required, uint Excluded, MoveTuning Tuning);
}
