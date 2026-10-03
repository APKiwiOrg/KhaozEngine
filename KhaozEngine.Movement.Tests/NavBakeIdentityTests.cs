using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NavBakeIdentityTests
{
    private const string GoldenEngine = "0.0.0-golden";
    private const string GoldenFingerprint = "b8d049d40ad365789f251269953c4ee1074440c24b80982f69b874f7cb9f08e3";

    private static readonly string[] ExcludedTuningFields = ["WalkSpeed", "RunSpeed", "AirMomentum"];

    private const string TuningGrows = "A new MoveTuning field needs encoding in NavBakeIdentity. Once a format " +
        "version has shipped, the layout change also needs a FormatVersion bump. Unreleased KENB v1 changes in place.";
    private const string OptionsGrow = "A new PhysicsNavBakeOptions field needs encoding in NavBakeIdentity. Once a " +
        "format version has shipped, the layout change also needs a FormatVersion bump. Unreleased KENB v1 changes in place.";
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static PhysicsNavBakeOptions GoldenOptions => new(
        MinX: -8f, MinZ: -8f, MaxX: 8f, MaxZ: 8f, CellSize: 0.25f,
        ProbeHeight: 10f, ProbeRange: 20f, MaxSlopeRadians: 0.8f,
        MaxCells: 4096, MaxLayerCells: 8192);

    private static MoveTuning GoldenTuning => new(WalkSpeed: 2f, RunSpeed: 5f, CapsuleHalfHeight: 0.75f,
        MaxSlopeRadians: 0.8f, CapsuleRadius: 0.3f);

    private static byte[] Digest(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static NavBakeExpectation Golden() => new(GoldenOptions,
        new NavBakeSources().Add("world", Digest(0x11)),
        [new NavBakeProfile("player", GoldenTuning, default)]);

    private static byte[] Encode(NavBakeExpectation expected, string engine = GoldenEngine) =>
        NavBakeIdentity.Encode(expected, engine);

    [Fact]
    public void IdentityIsCanonicalAcrossInsertionOrder()
    {
        var wide = new NavBakeProfile("wide", GoldenTuning with { CapsuleRadius = 0.6f }, new NavAreaFilter(0u, 2u));
        var player = new NavBakeProfile("player", GoldenTuning, default);
        var forward = new NavBakeExpectation(GoldenOptions,
            new NavBakeSources().Add("world", Digest(0x11)).Add("catalog", Digest(0x22)), [player, wide]);
        var reverse = new NavBakeExpectation(GoldenOptions,
            new NavBakeSources().Add("catalog", Digest(0x22)).Add("world", Digest(0x11)), [wide, player]);

        Assert.Equal(Encode(forward), Encode(reverse));
        Assert.Equal(new[] { "catalog", "world" }, forward.Sources.Labels);
        Assert.Equal(new[] { "catalog", "world" }, reverse.Sources.Labels);
    }

    [Fact]
    public void EveryRetainedTuningFieldChangesTheIdentity()
    {
        ConstructorInfo constructor = typeof(MoveTuning).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        List<ParameterInfo> all = [.. constructor.GetParameters()];
        List<ParameterInfo> retained = [.. all.Where(p => !ExcludedTuningFields.Contains(p.Name))];
        int stored = typeof(MoveTuning).GetFields(InstanceFields).Length;
        Assert.True(all.Count == 30, $"MoveTuning has {all.Count} constructor fields. {TuningGrows}");
        Assert.True(stored == 30, $"MoveTuning stores {stored} instance fields. {TuningGrows}");
        Assert.True(retained.Count == 27, $"MoveTuning retains {retained.Count} fields. {TuningGrows}");
        Assert.True(retained.Select(p => p.Name!).SequenceEqual(NavBakeIdentity.TuningFieldNames),
            $"MoveTuning field order differs from the encoder table. {TuningGrows}");

        object?[] baseline = all.Select(p => Read(GoldenTuning, p.Name!)).ToArray();
        byte[] reference = EncodeTuning(constructor, baseline);
        foreach (ParameterInfo field in all)
        {
            object?[] args = (object?[])baseline.Clone();
            args[field.Position] = Nudge(args[field.Position]!);
            byte[] changed = EncodeTuning(constructor, args);
            if (ExcludedTuningFields.Contains(field.Name))
                Assert.True(reference.AsSpan().SequenceEqual(changed), $"{field.Name} must not change the identity.");
            else
                Assert.False(reference.AsSpan().SequenceEqual(changed), $"{field.Name} does not change the identity. {TuningGrows}");
        }
    }

    [Fact]
    public void EveryOptionFieldChangesTheIdentity()
    {
        ConstructorInfo constructor = typeof(PhysicsNavBakeOptions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        List<ParameterInfo> fields = [.. constructor.GetParameters()];
        int stored = typeof(PhysicsNavBakeOptions).GetFields(InstanceFields).Length;
        Assert.True(fields.Count == 13, $"PhysicsNavBakeOptions has {fields.Count} constructor fields. {OptionsGrow}");
        Assert.True(stored == 14, $"PhysicsNavBakeOptions stores {stored} instance fields. {OptionsGrow}");
        Assert.True(fields.Select(p => p.Name!).Append(nameof(PhysicsNavBakeOptions.SampleWater))
            .SequenceEqual(NavBakeIdentity.OptionFieldNames),
            $"PhysicsNavBakeOptions field order differs from the encoder table. {OptionsGrow}");

        PhysicsNavBakeOptions options = GoldenOptions;
        object?[] baseline = fields.Select(p => typeof(PhysicsNavBakeOptions).GetProperty(p.Name!)!.GetValue(options)).ToArray();
        byte[] reference = Encode(Golden() with { Options = (PhysicsNavBakeOptions)constructor.Invoke(baseline) });
        Assert.Equal(Encode(Golden()), reference);
        foreach (ParameterInfo field in fields)
        {
            object?[] args = (object?[])baseline.Clone();
            args[field.Position] = Nudge(args[field.Position]!);
            byte[] changed = Encode(Golden() with { Options = (PhysicsNavBakeOptions)constructor.Invoke(args) });
            Assert.False(reference.AsSpan().SequenceEqual(changed), $"Option {field.Name} does not change the identity. {OptionsGrow}");
        }
        byte[] wet = Encode(Golden() with { Options = options with { SampleWater = true } });
        Assert.False(reference.AsSpan().SequenceEqual(wet), $"Option SampleWater does not change the identity. {OptionsGrow}");
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)0xFF)]
    public void SampleWaterByteOtherThanZeroOrOneIsCorrupt(byte value)
    {
        byte[] expected = Encode(CorruptBase());
        byte[] stored = (byte[])expected.Clone();
        int sampleWater = 2 + GoldenEngine.Length + 13 * 4;
        Assert.Equal(0, stored[sampleWater]);
        stored[sampleWater] = value;

        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(stored, expected);

        Assert.Equal(NavBakeLoadStatus.Corrupt, status);
        Assert.Contains("SampleWater", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineVersionStripsBuildMetadata()
    {
        Assert.Equal("20.19.0", NavBakeIdentity.NormalizeEngineVersion("20.19.0+8dfe93941"));
        Assert.Equal("20.19.0", NavBakeIdentity.NormalizeEngineVersion("20.19.0"));
        Assert.NotEqual(Encode(Golden(), "20.19.0"), Encode(Golden(), "20.19.1"));

        string informational = typeof(NavBakeIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(NavBakeIdentity.NormalizeEngineVersion(informational), NavBakeIdentity.CurrentEngineVersion);
        Assert.DoesNotContain('+', NavBakeIdentity.CurrentEngineVersion);
        Assert.NotEmpty(NavBakeIdentity.CurrentEngineVersion);
    }

    [Theory]
    [InlineData("label-empty")]
    [InlineData("label-upper")]
    [InlineData("label-long")]
    [InlineData("label-space")]
    [InlineData("name-empty")]
    [InlineData("name-upper")]
    [InlineData("name-long")]
    [InlineData("name-space")]
    [InlineData("duplicate-label")]
    [InlineData("duplicate-label-hashed")]
    [InlineData("duplicate-name")]
    [InlineData("short-digest")]
    [InlineData("engine-empty")]
    public void LabelsNamesAndDigestsAreValidated(string fault)
    {
        string? label = fault switch
        {
            "label-empty" => "",
            "label-upper" => "A",
            "label-long" => new string('a', 65),
            "label-space" => "a b",
            _ => null,
        };
        string? name = fault switch
        {
            "name-empty" => "",
            "name-upper" => "A",
            "name-long" => new string('a', 65),
            "name-space" => "a b",
            _ => null,
        };

        Assert.Throws<ArgumentException>(() =>
        {
            var sources = new NavBakeSources();
            if (label is not null) sources.Add(label, Digest(0x11));
            else if (fault == "short-digest") sources.Add("world", new byte[31]);
            else sources.Add("world", Digest(0x11));
            if (fault == "duplicate-label") sources.Add("world", Digest(0x22));
            if (fault == "duplicate-label-hashed") sources.AddHashOf("world", new MemoryStream([1, 2, 3]));

            List<NavBakeProfile> profiles = [new NavBakeProfile(name ?? "player", GoldenTuning, default)];
            if (fault == "duplicate-name") profiles.Add(new NavBakeProfile("player", GoldenTuning with { CapsuleRadius = 0.5f }, default));
            Encode(new NavBakeExpectation(GoldenOptions, sources, profiles), fault == "engine-empty" ? "" : GoldenEngine);
        });
    }

    [Fact]
    public void BoundaryLabelsAndNamesAreAccepted()
    {
        string longest = new('a', 64);
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789._-/";
        var sources = new NavBakeSources().Add(longest, Digest(0x11)).Add(Alphabet, Digest(0x22)).Add("z", Digest(0x33));
        NavBakeProfile[] profiles = [new(longest, GoldenTuning, default), new(Alphabet, GoldenTuning, default)];

        byte[] bytes = Encode(new NavBakeExpectation(GoldenOptions, sources, profiles));

        Assert.Equal(NavBakeLoadStatus.Loaded, NavBakeIdentity.Compare(bytes, bytes).Status);
    }

    [Fact]
    public void SetBoundsAreValidated()
    {
        NavBakeProfile Profile(int i) => new($"p{i:000}", GoldenTuning, default);

        Assert.Equal("expected", Assert.Throws<ArgumentException>(() => Encode(Golden() with { Sources = new NavBakeSources() })).ParamName);
        Assert.Equal("expected", Assert.Throws<ArgumentException>(() => Encode(Golden() with { Profiles = [] })).ParamName);
        Assert.Equal("expected", Assert.Throws<ArgumentException>(() =>
            Encode(Golden() with { Profiles = [.. Enumerable.Range(0, 257).Select(Profile)] })).ParamName);
        Assert.Equal("expected", Assert.Throws<ArgumentException>(() =>
            Encode(Golden() with { Profiles = [Profile(1), Profile(1)] })).ParamName);
        Assert.Equal("expected", Assert.Throws<ArgumentException>(() =>
            Encode(Golden() with { Profiles = [new NavBakeProfile("A", GoldenTuning, default)] })).ParamName);

        byte[] most = Encode(Golden() with { Profiles = [.. Enumerable.Range(0, 256).Select(Profile)] });
        Assert.Equal(NavBakeLoadStatus.Loaded, NavBakeIdentity.Compare(most, most).Status);
    }

    [Fact]
    public void IdentityBytesMatchTheGoldenFingerprint()
    {
        // Every argument is a literal, so a changed declared default cannot move the golden without a layout change.
        var options = new PhysicsNavBakeOptions(MinX: -8f, MinZ: -8f, MaxX: 8f, MaxZ: 8f, CellSize: 0.25f,
            ProbeHeight: 10f, ProbeRange: 20f, MaxSlopeRadians: 0.8f, MaxCells: 4096, MaxLayerCells: 8192,
            MaxSurfacesPerColumn: 4, EdgeProbeSeconds: 1f / 30f, MaxEdgeProbeSteps: 64)
        {
            SampleWater = false,
        };
        var tuning = new MoveTuning(WalkSpeed: 2f, RunSpeed: 5f, CapsuleHalfHeight: 0.75f, MaxSlopeRadians: 0.8f,
            CapsuleRadius: 0.3f, Gravity: 25f, JumpSpeed: 9.79796f, MaxFallSpeed: 50f, CoyoteTime: 0.1f,
            JumpBuffer: 0.1f, AirControl: 1f, GroundedEpsilon: 0.3f, StepHeight: 0.4f,
            WadeStartDepthFraction: 0.15f, WadeEndDepthFraction: 0.65f, WadeMinSpeedScale: 0.45f,
            SwimEnterDepthFraction: 0.65f, SwimExitDepthFraction: 0.55f, SwimSpeed: 2.5f,
            SwimSurfaceSubmersionFraction: 0.6f, SwimBuoyancyStiffness: 8f, MaxStepClimbSpeed: 3.5f,
            AirMomentum: false, AirBrakeAccel: 0f, FacingTurnSpeed: float.PositiveInfinity,
            TractionHysteresisRadians: MathF.PI * 3f / 180f, SlideFrictionRampRadians: MathF.PI * 8f / 180f,
            StrafeSpeedScale: 1f, BackpedalSpeedScale: 1f, BackpedalAllowsRun: true);
        var golden = new NavBakeExpectation(options, new NavBakeSources().Add("world", Digest(0x11)),
            [new NavBakeProfile("player", tuning, new NavAreaFilter(0u, 0u)) { Aquatic = false }]);

        byte[] identity = Encode(golden);

        Assert.Equal((ushort)1, NavBakeIdentity.FormatVersion);
        Assert.Equal(GoldenFingerprint, Convert.ToHexStringLower(SHA256.HashData(identity)));
    }

    [Fact]
    public void AquaticFlagChangesTheIdentity()
    {
        NavBakeExpectation golden = Golden();
        byte[] ground = Encode(golden);
        byte[] aquatic = Encode(golden with { Profiles = [golden.Profiles[0] with { Aquatic = true }] });

        Assert.False(new NavBakeProfile("player", GoldenTuning, default).Aquatic);
        Assert.False(ground.AsSpan().SequenceEqual(aquatic));
        Assert.Equal(ground.Length, aquatic.Length);
    }

    [Theory]
    [InlineData("aquatic-2")]
    [InlineData("aquatic-255")]
    public void AquaticFlagByteOtherThanZeroOrOneIsCorrupt(string fault)
    {
        byte[] expected = Encode(CorruptBase());
        byte[] stored = Fault((byte[])expected.Clone(), fault);

        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(stored, expected);

        Assert.Equal(NavBakeLoadStatus.Corrupt, status);
        Assert.Contains("Aquatic", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleAquaticFlagIsRefusedNamingIt()
    {
        NavBakeExpectation golden = Golden();
        byte[] ground = Encode(golden);
        byte[] aquatic = Encode(golden with { Profiles = [golden.Profiles[0] with { Aquatic = true }] });

        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(aquatic, ground);
        (NavBakeLoadStatus reverse, string reverseDetail) = NavBakeIdentity.Compare(ground, aquatic);

        Assert.Equal(NavBakeLoadStatus.ProfilesChanged, status);
        Assert.Equal("Profile 'player' field Aquatic: bake True, expected False.", detail);
        Assert.Equal(NavBakeLoadStatus.ProfilesChanged, reverse);
        Assert.Contains("field Aquatic: bake False, expected True", reverseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTuningRestoresTheProbeOverrides()
    {
        // Every field distinct, so a reader that swaps two fields cannot round trip.
        ConstructorInfo constructor = typeof(MoveTuning).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        object?[] args = constructor.GetParameters()
            .Select(p => p.ParameterType == typeof(bool) ? (object)(p.Name != "BackpedalAllowsRun") : 0.5f + p.Position)
            .ToArray();
        var tuning = (MoveTuning)constructor.Invoke(args);
        Assert.True(tuning.AirMomentum);
        Assert.False(tuning.BackpedalAllowsRun);
        var writer = new NavBakeWriter();
        NavBakeIdentity.WriteTuning(writer, tuning);
        byte[] bytes = writer.ToArray();
        var reader = new NavBakeReader(bytes);

        Assert.True(NavBakeIdentity.ReadTuning(ref reader, out MoveTuning read));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false }, read);

        var shortReader = new NavBakeReader(bytes.AsSpan(0, bytes.Length - 1));
        Assert.False(NavBakeIdentity.ReadTuning(ref shortReader, out _));
    }

    [Fact]
    public void HashedSourcesMatchSha256()
    {
        byte[] content = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
        byte[] expected = SHA256.HashData(content);
        var sources = new NavBakeSources()
            .Add("direct", expected)
            .AddHashOf("span", content)
            .AddHashOf("stream", new MemoryStream(content));

        IReadOnlyList<(string Label, byte[] Digest)> snapshot = sources.Snapshot();

        Assert.Equal(new[] { "direct", "span", "stream" }, snapshot.Select(e => e.Label));
        Assert.All(snapshot, e => Assert.Equal(expected, e.Digest));
        snapshot[0].Digest[0] ^= 0xFF;
        Assert.Equal(expected, sources.Snapshot()[0].Digest);
    }

    [Fact]
    public void EqualIdentitiesAreLoaded()
    {
        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(Encode(Golden()), Encode(Golden()));

        Assert.Equal(NavBakeLoadStatus.Loaded, status);
        Assert.Equal("", detail);
    }

    [Theory]
    [InlineData("engine", NavBakeLoadStatus.EngineChanged, GoldenEngine)]
    [InlineData("cell-size", NavBakeLoadStatus.OptionsChanged, "CellSize")]
    [InlineData("digest", NavBakeLoadStatus.SourcesChanged, "world")]
    [InlineData("extra-label", NavBakeLoadStatus.SourcesChanged, "catalog")]
    [InlineData("missing-profile", NavBakeLoadStatus.ProfilesChanged, "player")]
    [InlineData("gravity", NavBakeLoadStatus.ProfilesChanged, "Gravity")]
    [InlineData("areas", NavBakeLoadStatus.ProfilesChanged, "Excluded")]
    [InlineData("engine-and-options", NavBakeLoadStatus.EngineChanged, GoldenEngine)]
    [InlineData("options-and-profiles", NavBakeLoadStatus.OptionsChanged, "ProbeRange")]
    [InlineData("sample-water", NavBakeLoadStatus.OptionsChanged, "Option SampleWater: bake False, expected True")]
    public void CompareNamesTheFirstDifference(string change, NavBakeLoadStatus status, string detailFragment)
    {
        NavBakeExpectation golden = Golden();
        byte[] stored = Encode(golden);
        byte[] expected = change switch
        {
            "engine" => Encode(golden, "0.0.1"),
            "cell-size" => Encode(golden with { Options = GoldenOptions with { CellSize = 0.5f } }),
            "digest" => Encode(golden with { Sources = new NavBakeSources().Add("world", Digest(0x12)) }),
            "extra-label" => Encode(golden with
            {
                Sources = new NavBakeSources().Add("world", Digest(0x11)).Add("catalog", Digest(0x22)),
            }),
            "missing-profile" => stored,
            "gravity" => Encode(golden with { Profiles = [new NavBakeProfile("player", GoldenTuning with { Gravity = 24f }, default)] }),
            "areas" => Encode(golden with { Profiles = [new NavBakeProfile("player", GoldenTuning, new NavAreaFilter(0u, 4u))] }),
            "sample-water" => Encode(golden with { Options = GoldenOptions with { SampleWater = true } }),
            "engine-and-options" => Encode(golden with { Options = GoldenOptions with { CellSize = 0.5f } }, "0.0.1"),
            "options-and-profiles" => Encode(golden with
            {
                Options = GoldenOptions with { ProbeRange = 21f },
                Profiles = [new NavBakeProfile("player", GoldenTuning with { Gravity = 24f }, default)],
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        if (change == "missing-profile")
            stored = Encode(golden with { Profiles = [new NavBakeProfile("wide", GoldenTuning, default)] });

        (NavBakeLoadStatus actual, string detail) = NavBakeIdentity.Compare(stored, expected);

        Assert.Equal(status, actual);
        Assert.Contains(detailFragment, detail, StringComparison.Ordinal);
        if (change == "gravity") Assert.Contains("player", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unsorted-labels", "source labels out of order")]
    [InlineData("duplicate-profile-name", "profile names out of order or repeated")]
    [InlineData("label-byte-upper", "source label outside the label set")]
    [InlineData("overlapping-filter", "overlapping area bits in profile 'alpha'")]
    [InlineData("trailing-byte", "1 trailing bytes")]
    [InlineData("cut-short", "truncated or non-canonical tuning in profile 'bravo'")]
    [InlineData("empty", "truncated in the engine version")]
    [InlineData("bool-byte", "truncated or non-canonical tuning in profile 'alpha'")]
    [InlineData("engine-utf8", "invalid engine version")]
    [InlineData("profile-order", "profile names out of order or repeated")]
    [InlineData("duplicate-label", "source labels out of order or repeated")]
    [InlineData("source-count-0", "impossible source count 0")]
    [InlineData("source-count-257", "impossible source count 257")]
    [InlineData("source-count-65535", "impossible source count 65535")]
    [InlineData("profile-count-0", "impossible profile count 0")]
    [InlineData("profile-count-257", "impossible profile count 257")]
    [InlineData("profile-count-65535", "impossible profile count 65535")]
    [InlineData("label-length-65", "source label outside the label set")]
    public void NonCanonicalStoredIdentityIsCorruptAndNeverThrows(string fault, string detailFragment)
    {
        byte[] expected = Encode(CorruptBase());
        byte[] stored = Fault((byte[])expected.Clone(), fault);

        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(stored, expected);

        Assert.Equal(NavBakeLoadStatus.Corrupt, status);
        Assert.Contains(detailFragment, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPrefixOfAStoredIdentityIsCorrupt()
    {
        byte[] expected = Encode(CorruptBase());

        for (int length = 0; length < expected.Length; length++)
        {
            (NavBakeLoadStatus status, _) = NavBakeIdentity.Compare(expected.AsSpan(0, length), expected);
            Assert.True(status == NavBakeLoadStatus.Corrupt, $"Prefix of {length} bytes returned {status}.");
        }
    }

    // Golden options and tuning with two sources and two equal-length profile names, so entries edit in place.
    private static NavBakeExpectation CorruptBase() => new(GoldenOptions,
        new NavBakeSources().Add("world", Digest(0x11)).Add("catalog", Digest(0x22)),
        [new NavBakeProfile("alpha", GoldenTuning, default), new NavBakeProfile("bravo", GoldenTuning, default)]);

    private static byte[] Fault(byte[] bytes, string fault)
    {
        const int Options = 13 * 4 + 1;
        const int Tuning = 26 * 4 + 1;
        int engine = 2 + GoldenEngine.Length;
        int sources = engine + Options + 2;
        int catalog = sources, catalogLength = 1 + 7 + 32;
        int world = catalog + catalogLength, worldLength = 1 + 5 + 32;
        int profiles = world + worldLength + 2;
        int profileLength = 1 + 5 + 8 + 1 + Tuning;
        int alpha = profiles, bravo = profiles + profileLength;
        switch (fault)
        {
            case "unsorted-labels":
                byte[] swapped = [.. bytes.AsSpan(world, worldLength), .. bytes.AsSpan(catalog, catalogLength)];
                swapped.CopyTo(bytes, catalog);
                return bytes;
            case "duplicate-profile-name":
                bytes.AsSpan(alpha + 1, 5).CopyTo(bytes.AsSpan(bravo + 1, 5));
                return bytes;
            case "label-byte-upper":
                bytes[world + 1] = (byte)'W';
                return bytes;
            case "overlapping-filter":
                bytes[alpha + 6] = 0x01;
                bytes[alpha + 10] = 0x01;
                return bytes;
            case "trailing-byte":
                return [.. bytes, 0];
            case "cut-short":
                return bytes[..^1];
            case "empty":
                return [];
            case "bool-byte":
                bytes[alpha + 15 + Tuning - 1] = 2;
                return bytes;
            case "engine-utf8":
                bytes[2] = 0xFF;
                return bytes;
            case "profile-order":
                byte[] reordered = [.. bytes.AsSpan(bravo, profileLength), .. bytes.AsSpan(alpha, profileLength)];
                reordered.CopyTo(bytes, alpha);
                return bytes;
            case "duplicate-label":
                return [.. bytes.AsSpan(0, world), .. bytes.AsSpan(catalog, catalogLength), .. bytes.AsSpan(world + worldLength)];
            case "source-count-0" or "source-count-257" or "source-count-65535":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sources - 2), ushort.Parse(fault[13..], CultureInfo.InvariantCulture));
                return bytes;
            case "profile-count-0" or "profile-count-257" or "profile-count-65535":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(profiles - 2), ushort.Parse(fault[14..], CultureInfo.InvariantCulture));
                return bytes;
            case "aquatic-2" or "aquatic-255":
                Assert.Equal(0, bytes[alpha + 14]);
                bytes[alpha + 14] = byte.Parse(fault[8..], CultureInfo.InvariantCulture);
                return bytes;
            case "label-length-65":
                bytes[world] = 65;
                return bytes;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    private static byte[] EncodeTuning(ConstructorInfo constructor, object?[] args)
    {
        var tuning = (MoveTuning)constructor.Invoke(args);
        return Encode(Golden() with { Profiles = [new NavBakeProfile("player", tuning, default)] });
    }

    private static object? Read(MoveTuning tuning, string name) =>
        typeof(MoveTuning).GetProperty(name)!.GetValue(tuning);

    private static object Nudge(object value) => value switch
    {
        float f when MathF.BitIncrement(f) != f => MathF.BitIncrement(f),
        float f => MathF.BitDecrement(f),
        int i => i + 1,
        bool b => !b,
        _ => throw new InvalidOperationException($"Unexpected field type {value.GetType()}."),
    };
}
