using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.Movement.TileWorldMovementNavigationTests;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileInstallationCompatibilityTests
{
    private const string WorldId = "filtered-installation-baseline";
    private const int HeaderLength = 52;
    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n"
    };

    [Fact]
    public void RegistrationJsonUsesExplicitLfFormatting()
    {
        // Requiring shared options catches the platform-default writer even on an LF host.
        var optionsField = Assert.Single(typeof(TileInstallationCompatibilityTests)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static),
            field => field.FieldType == typeof(JsonSerializerOptions));
        var options = Assert.IsType<JsonSerializerOptions>(optionsField.GetValue(null));
        Assert.True(options.WriteIndented);
        Assert.Equal("\n", options.NewLine);
        Assert.DoesNotContain('\r', RegistrationJson());
        Assert.DoesNotContain('\r', RegistrationJson(includeAll: true));
    }

    [Fact]
    public void UnfilteredInstallationMatchesPreFeatureBaseline()
    {
        string actualRegistrationJson = RegistrationJson();
        string frozenRegistrationJson = Encoding.UTF8.GetString(ReadFixture("json"));
        Assert.Equal(frozenRegistrationJson, actualRegistrationJson);
        Assert.Equal(ReadFixture("navbake"), NormalizeNavigationFile(NavigationFile()));
    }

    [Fact]
    public void AllTrueAndDefaultMatchPreFeatureBaseline()
    {
        string baseline = Encoding.UTF8.GetString(ReadFixture("json"));
        Assert.Equal(baseline, RegistrationJson());
        Assert.Equal(baseline, RegistrationJson(includeAll: true));
        byte[] original = NavigationFile();
        byte[] allTrue = NavigationFile(includeAll: true);
        Assert.Equal(original, allTrue);
        Assert.Equal(ReadFixture("navbake"), NormalizeNavigationFile(allTrue));
    }

    [Fact]
    public void HistoricalNavigationOracleIgnoresOnlyEngineVersion()
    {
        byte[] frozen = ReadFixture("navbake");
        byte[] shortVersion = WithEngineVersion(frozen, "1.0.0");
        byte[] longVersion = WithEngineVersion(frozen, "2100.123.456-preview.\u03b1");
        Assert.NotEqual(shortVersion.Length, longVersion.Length);
        Assert.Equal(frozen, NormalizeNavigationFile(shortVersion));
        Assert.Equal(frozen, NormalizeNavigationFile(longVersion));
        Assert.Equal(frozen, NormalizeNavigationFile(frozen));
    }

    // Offsets name fields in the frozen file, not values derived from the current writer.
    [Theory]
    [InlineData("source label", 122)]
    [InlineData("option MinX", 66)]
    [InlineData("profile tuning", 181)]
    [InlineData("collider digest", 131)]
    [InlineData("column surface height", 513)]
    [InlineData("graph exit mask", 1916)]
    [InlineData("header format", 4)]
    [InlineData("header flags", 6)]
    [InlineData("header checksum", 20)]
    public void HistoricalNavigationOracleRejectsOtherChanges(string section, int offset)
    {
        byte[] frozen = ReadFixture("navbake");
        byte[] changed = (byte[])frozen.Clone();
        changed[offset] ^= 1;
        Assert.False(frozen.AsSpan().SequenceEqual(NormalizeNavigationFile(changed)),
            $"The historical oracle must retain changes to {section}.");
    }

    [Theory]
    [InlineData("short header")]
    [InlineData("magic")]
    [InlineData("short identity")]
    [InlineData("oversized identity")]
    [InlineData("payload length")]
    [InlineData("empty version")]
    [InlineData("oversized version")]
    [InlineData("trailing byte")]
    public void HistoricalNavigationOracleRejectsInvalidFraming(string mutation)
    {
        byte[] changed = ReadFixture("navbake");
        switch (mutation)
        {
            case "short header": changed = changed[..HeaderLength]; break;
            case "magic": changed[0] ^= 1; break;
            case "short identity": BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(8), 1); break;
            case "oversized identity": BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(8), uint.MaxValue); break;
            case "payload length": BinaryPrimitives.WriteUInt64LittleEndian(changed.AsSpan(12), ulong.MaxValue); break;
            case "empty version": BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(HeaderLength), 0); break;
            case "oversized version": BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(HeaderLength), ushort.MaxValue); break;
            case "trailing byte": changed = [.. changed, 0]; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Throws<InvalidDataException>(() => NormalizeNavigationFile(changed));
    }

    [Fact]
    public void HistoricalNavigationOracleRejectsInvalidVersionEncoding()
    {
        byte[] changed = ReadFixture("navbake");
        changed[HeaderLength + 2] = 0xff;
        Assert.Throws<DecoderFallbackException>(() => NormalizeNavigationFile(changed));
    }

    // This is a historical oracle only. Loading still authenticates the current engine identity.
    internal static byte[] NormalizeNavigationFile(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < HeaderLength + 2 || !file.AsSpan(0, 4).SequenceEqual("KENB"u8))
            throw new InvalidDataException("The navigation file header is truncated or invalid.");
        uint identityLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(12));
        if (identityLength < 2 || identityLength > file.Length - HeaderLength ||
            payloadLength != (ulong)(file.Length - HeaderLength - identityLength))
            throw new InvalidDataException("The navigation file lengths do not frame the complete file.");
        int versionLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(HeaderLength));
        if (versionLength == 0 || versionLength > identityLength - 2)
            throw new InvalidDataException("The engine version is not contained in the identity block.");
        _ = new UTF8Encoding(false, true).GetString(file, HeaderLength + 2, versionLength);
        ReadOnlySpan<byte> golden = "0.0.0-golden"u8;
        byte[] normalized = [.. file.AsSpan(0, HeaderLength), .. new byte[2], .. golden,
            .. file.AsSpan(HeaderLength + 2 + versionLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(normalized.AsSpan(HeaderLength), (ushort)golden.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(8),
            checked((uint)(identityLength - versionLength + golden.Length)));
        return normalized;
    }

    private static string RegistrationJson(bool includeAll = false)
    {
        var colliders = TileWorldColliders.Build(TileWorldPhysicsTestData.EveryKindWorld(),
            TileWorldPhysicsTestData.Catalogs(), WorldId);
        string actualColliderHash = Convert.ToHexStringLower(colliders.Hash);
        Assert.Equal("6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851", actualColliderHash);
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = includeAll ? colliders.AddTo(physics, _ => true) : colliders.AddTo(physics);
        Assert.Equal(actualColliderHash, Convert.ToHexStringLower(colliders.Hash));
        Assert.Equal(Enumerable.Range(0, colliders.Colliders.Count), installed.ColliderIndices);
        var registration = installed.CreateMovementRegistration(WorldId);
        Assert.Equal(colliders.Colliders.Count, installed.Handles.Count);
        using var lease = physics.AcquireQueryReadLease();
        var rows = new List<object>();
        var exclusions = new List<int>();
        for (int index = 0; index < colliders.Colliders.Count; index++)
        {
            var collider = colliders.Colliders[index];
            bool excluded = registration.ExcludedStatics.Contains(installed.Handles[index]);
            var availability = registration.TryBind(lease, installed.Handles[index], 0, out var owner);
            string localId;
            if (excluded)
            {
                Assert.Equal(TileColliderKind.Ground, collider.Kind);
                Assert.Equal(MovementAvailability.Unresolved, availability);
                // EveryKindWorld has one ground region. Its excluded owner is also pinned by the digest.
                localId = "ground/0/0/0";
                exclusions.Add(index);
            }
            else
            {
                Assert.Equal(MovementAvailability.Known, availability);
                Assert.Equal(WorldId, owner.WorldId);
                Assert.Equal(MovementAvailability.Known,
                    registration.TryBind(lease, installed.Handles[index], int.MaxValue, out var lastOwner));
                Assert.Equal(owner, lastOwner);
                localId = owner.LocalId;
            }
            var bounds = TileColliderProvenance.Capture(localId, collider);
            rows.Add(new
            {
                ColliderIndex = index,
                Kind = collider.Kind.ToString(),
                WorldId,
                OwnerLocalId = localId,
                FirstFeatureId = 0,
                LastFeatureId = int.MaxValue,
                MinimumBits = Bits(bounds.AbsoluteMinimum),
                MaximumBits = Bits(bounds.AbsoluteMaximum),
                Excluded = excluded
            });
        }
        Assert.Equal(installed.GroundHandles, registration.ExcludedStatics);
        Assert.Equal(colliders.MovementExtent, registration.Extent);
        var extent = registration.Extent;
        return JsonSerializer.Serialize(new
        {
            ColliderHash = actualColliderHash,
            WorldId,
            Rows = rows,
            ExcludedColliderIndices = exclusions,
            Extent = new
            {
                MinimumBits = Bits(extent.Minimum),
                MaximumBits = Bits(extent.Maximum),
                AnalyticMinimumYBits = Bits(extent.AnalyticMinimumY),
                AnalyticMaximumYBits = Bits(extent.AnalyticMaximumY)
            },
            registration.SemanticDigest
        }, RegistrationJsonOptions) + "\n";
    }

    private static uint[] Bits(Vector3 value) =>
        [BitConverter.SingleToUInt32Bits(value.X), BitConverter.SingleToUInt32Bits(value.Y), BitConverter.SingleToUInt32Bits(value.Z)];

    private static uint? Bits(float? value) => value.HasValue ? BitConverter.SingleToUInt32Bits(value.Value) : null;

    private static byte[] NavigationFile(bool includeAll = false)
    {
        using var scene = new Scene(TileWorldNavigationBakeTests.DeckDoorWorld(), TileWorldNavigationBakeTests.Origin,
            include: includeAll ? _ => true : null);
        using var capture = scene.Capture(TileWorldNavigationBakeTests.Options,
            feet => scene.Colliders.Medium.MediumAt(feet.X, feet.Z, feet.Y).InWater ? Wet : Dry);
        var sources = new NavBakeSources().Add("colliders", scene.Colliders.Hash);
        var bake = GroundNavigationBake.Create(capture, sources, TileWorldNavigationBakeTests.Profiles);
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        return stream.ToArray();
    }

    private static byte[] ReadFixture(string extension)
    {
        using Stream stream = typeof(TileInstallationCompatibilityTests).Assembly.GetManifestResourceStream(
            $"KhaozEngine.Tests.Fixtures.TileInstallationBaseline.{extension}")
            ?? throw new InvalidOperationException("The frozen compatibility fixture is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] WithEngineVersion(byte[] file, string version)
    {
        using var reader = new BinaryReader(new MemoryStream(file));
        reader.BaseStream.Position = 8;
        uint identityLength = reader.ReadUInt32();
        reader.BaseStream.Position = HeaderLength;
        int oldVersionLength = reader.ReadUInt16();
        byte[] encoded = Encoding.UTF8.GetBytes(version);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(file, 0, 8);
        writer.Write(checked((uint)(identityLength - oldVersionLength + encoded.Length)));
        writer.Write(file, 12, HeaderLength - 12);
        writer.Write(checked((ushort)encoded.Length));
        writer.Write(encoded);
        int remainder = HeaderLength + 2 + oldVersionLength;
        writer.Write(file, remainder, file.Length - remainder);
        return stream.ToArray();
    }
}
