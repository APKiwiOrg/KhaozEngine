using System;
using System.Collections.Generic;
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
    private const string GoldenFingerprint = "a0a927cec3bf88a763041c829767efc6be1415e4e75070cc378948cfd43666e3";

    private static readonly string[] ExcludedTuningFields = ["WalkSpeed", "RunSpeed", "AirMomentum"];

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
        const string Grows = "A new MoveTuning field needs encoding in NavBakeIdentity and a format version bump.";
        Assert.True(all.Count == 30, $"MoveTuning has {all.Count} fields. {Grows}");
        Assert.True(retained.Count == 27, $"MoveTuning retains {retained.Count} fields. {Grows}");
        Assert.Equal(retained.Select(p => p.Name!), NavBakeIdentity.TuningFieldNames);

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
                Assert.False(reference.AsSpan().SequenceEqual(changed), $"{field.Name} does not change the identity. {Grows}");
        }
    }

    [Fact]
    public void EveryOptionFieldChangesTheIdentity()
    {
        ConstructorInfo constructor = typeof(PhysicsNavBakeOptions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        List<ParameterInfo> fields = [.. constructor.GetParameters()];
        Assert.Equal(13, fields.Count);
        Assert.Equal(fields.Select(p => p.Name!), NavBakeIdentity.OptionFieldNames);

        PhysicsNavBakeOptions options = GoldenOptions;
        object?[] baseline = fields.Select(p => typeof(PhysicsNavBakeOptions).GetProperty(p.Name!)!.GetValue(options)).ToArray();
        byte[] reference = Encode(Golden() with { Options = (PhysicsNavBakeOptions)constructor.Invoke(baseline) });
        Assert.Equal(Encode(Golden()), reference);
        foreach (ParameterInfo field in fields)
        {
            object?[] args = (object?[])baseline.Clone();
            args[field.Position] = Nudge(args[field.Position]!);
            byte[] changed = Encode(Golden() with { Options = (PhysicsNavBakeOptions)constructor.Invoke(args) });
            Assert.False(reference.AsSpan().SequenceEqual(changed), $"Option {field.Name} does not change the identity.");
        }
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

        Assert.Throws<ArgumentException>(() => Encode(Golden() with { Sources = new NavBakeSources() }));
        Assert.Throws<ArgumentException>(() => Encode(Golden() with { Profiles = [] }));
        Assert.Throws<ArgumentException>(() => Encode(Golden() with { Profiles = [.. Enumerable.Range(0, 257).Select(Profile)] }));

        byte[] most = Encode(Golden() with { Profiles = [.. Enumerable.Range(0, 256).Select(Profile)] });
        Assert.Equal(NavBakeLoadStatus.Loaded, NavBakeIdentity.Compare(most, most).Status);
    }

    [Fact]
    public void IdentityBytesMatchTheGoldenFingerprint()
    {
        byte[] identity = Encode(Golden());

        Assert.Equal((ushort)1, NavBakeIdentity.FormatVersion);
        Assert.Equal(GoldenFingerprint, Convert.ToHexStringLower(SHA256.HashData(identity)));
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
    public void NonCanonicalStoredIdentityIsCorruptAndNeverThrows(string fault, string detailFragment)
    {
        // Golden options and tuning with two sources and two equal-length profile names, so entries edit in place.
        var expectation = new NavBakeExpectation(GoldenOptions,
            new NavBakeSources().Add("world", Digest(0x11)).Add("catalog", Digest(0x22)),
            [new NavBakeProfile("alpha", GoldenTuning, default), new NavBakeProfile("bravo", GoldenTuning, default)]);
        byte[] expected = Encode(expectation);
        byte[] stored = Fault((byte[])expected.Clone(), fault);

        (NavBakeLoadStatus status, string detail) = NavBakeIdentity.Compare(stored, expected);

        Assert.Equal(NavBakeLoadStatus.Corrupt, status);
        Assert.Contains(detailFragment, detail, StringComparison.Ordinal);
    }

    private static byte[] Fault(byte[] bytes, string fault)
    {
        const int Options = 13 * 4;
        const int Tuning = 26 * 4 + 1;
        int engine = 2 + GoldenEngine.Length;
        int sources = engine + Options + 2;
        int catalog = sources, catalogLength = 1 + 7 + 32;
        int world = catalog + catalogLength, worldLength = 1 + 5 + 32;
        int profiles = world + worldLength + 2;
        int profileLength = 1 + 5 + 8 + Tuning;
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
                bytes[alpha + 14 + Tuning - 1] = 2;
                return bytes;
            case "engine-utf8":
                bytes[2] = 0xFF;
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
