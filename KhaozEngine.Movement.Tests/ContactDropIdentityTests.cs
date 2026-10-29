using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class ContactDropIdentityTests
{
    [Theory]
    [InlineData(false, NavBakeLoadStatus.ProfilesChanged)]
    [InlineData(true, NavBakeLoadStatus.EngineChanged)]
    public void EngineDifferencePrecedesACrossFormatAllowanceDifference(bool otherEngine, NavBakeLoadStatus status)
    {
        var expected = new NavBakeExpectation(ContactDropFixture.Options, Sources(), [Profile()]);
        byte[] stored = NavBakeIdentity.Encode(expected, "controlled-engine");
        byte[] changed = NavBakeIdentity.Encode(expected with { Profiles = [Profile() with { MaxDropMetres = 0 }] },
            otherEngine ? "other-engine" : "controlled-engine");
        var difference = NavBakeIdentity.Compare(stored, changed, 3, 2);
        Assert.Equal(status, difference.Status);
        if (!otherEngine) Assert.Contains("MaxDropMetres", difference.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedOrDisabledOnlyFormatThreeIdentityIsCorrupt(bool truncate)
    {
        byte[] identity = NavBakeIdentity.Encode(new(ContactDropFixture.Options, Sources(), [Profile()]), "controlled-engine");
        if (truncate) identity = identity[..^1];
        else
        {
            int tail = identity.Length - 13;
            BinaryPrimitives.WriteSingleLittleEndian(identity.AsSpan(tail), 0);
            BinaryPrimitives.WriteSingleLittleEndian(identity.AsSpan(tail + 4), 1);
            BinaryPrimitives.WriteSingleLittleEndian(identity.AsSpan(tail + 8), 1);
            identity[tail + 12] = 0;
        }
        Assert.False(NavBakeIdentity.TryDecode(identity, out _, out _, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroGravityAndTerminalSpeedRetainTheExistingKernelPreflightRefusal(bool terminal)
    {
        using var fixture = new ContactDropFixture();
        MoveTuning tuning = terminal ? ContactDropFixture.Tuning with { MaxFallSpeed = 0 } :
            ContactDropFixture.Tuning with { Gravity = 0 };
        Assert.Throws<ArgumentException>(() => fixture.Capture.BuildProfile(tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 }));
        using var stream = new NeverReadStream();
        Assert.Throws<ArgumentException>(() => GroundNavigationBake.Load(stream,
            new(ContactDropFixture.Options, Sources(), [Profile() with { Tuning = tuning }])));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidAllowanceIsRefusedBeforeReading(float allowance)
    {
        var profile = Profile() with { MaxDropMetres = allowance };
        var expected = new NavBakeExpectation(ContactDropFixture.Options, Sources(), [profile]);
        using var source = new NeverReadStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundNavigationBake.Load(source, expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedProfileSelectionIsRefusedBeforeReading(bool aquatic)
    {
        var profile = Profile() with { Aquatic = aquatic };
        if (!aquatic) profile = profile with { Tuning = profile.Tuning with { Controller = MovementControllerKind.Legacy, Water = default } };
        using var source = new NeverReadStream();
        Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Load(source,
            new(ContactDropFixture.Options, Sources(), [profile])));
    }

    [Fact]
    public void EnabledIdentityUsesFormatThreeAndNamesEveryFlightInput()
    {
        var profile = Profile();
        var expected = new NavBakeExpectation(ContactDropFixture.Options, Sources(), [profile]);
        Assert.Equal((ushort)3, NavBakeIdentity.FormatFor(expected.Profiles));
        byte[] identity = NavBakeIdentity.Encode(expected, "controlled-engine");
        Assert.True(NavBakeIdentity.TryDecode(identity, out var decoded, out _, 3));
        Assert.Equal(6, decoded.Profiles[0].MaxDropMetres);
        foreach (string field in new[] { "MaxDropMetres", "WalkSpeed", "RunSpeed", "AirMomentum" })
        {
            var changed = field switch
            {
                "MaxDropMetres" => profile with { MaxDropMetres = 5 },
                "WalkSpeed" => profile with { Tuning = profile.Tuning with { WalkSpeed = 4 } },
                "RunSpeed" => profile with { Tuning = profile.Tuning with { RunSpeed = 6 } },
                _ => profile with { Tuning = profile.Tuning with { AirMomentum = !profile.Tuning.AirMomentum } },
            };
            var result = NavBakeIdentity.Compare(identity,
                NavBakeIdentity.Encode(expected with { Profiles = [changed] }, "controlled-engine"), 3, 3);
            Assert.Equal(NavBakeLoadStatus.ProfilesChanged, result.Status);
            Assert.Contains(field, result.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DisabledIdentityKeepsFrozenBytesAndCanonicalSignedZero()
    {
        var profile = Profile() with { MaxDropMetres = 0 };
        var expected = new NavBakeExpectation(ContactDropFixture.Options, Sources(), [profile]);
        Assert.Equal((ushort)2, NavBakeIdentity.FormatFor(expected.Profiles));
        var changed = profile with
        {
            MaxDropMetres = BitConverter.UInt32BitsToSingle(0x80000000),
            Tuning = profile.Tuning with { WalkSpeed = 0, RunSpeed = 0, AirMomentum = true },
        };
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(changed.MaxDropMetres));
        Assert.Equal(NavBakeIdentity.Encode(expected, "controlled-engine"),
            NavBakeIdentity.Encode(expected with { Profiles = [changed] }, "controlled-engine"));
    }

    [Theory]
    [InlineData("allowance")]
    [InlineData("walk")]
    [InlineData("run")]
    [InlineData("momentum")]
    public void DisabledFormatThreeFieldsMustBeCanonical(string fault)
    {
        var profiles = new[] { Profile(), Profile() with { Name = "zero", MaxDropMetres = 0 } };
        byte[] identity = NavBakeIdentity.Encode(new(ContactDropFixture.Options, Sources(), profiles), "controlled-engine");
        int fields = identity.Length - 13;
        if (fault == "allowance") BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(fields), 0x80000000);
        if (fault == "walk") BinaryPrimitives.WriteSingleLittleEndian(identity.AsSpan(fields + 4), 2);
        if (fault == "run") BinaryPrimitives.WriteSingleLittleEndian(identity.AsSpan(fields + 8), 2);
        if (fault == "momentum") identity[fields + 12] = 1;
        Assert.False(NavBakeIdentity.TryDecode(identity, out _, out _, 3));
    }

    internal static NavBakeProfile Profile() => new("player", ContactDropFixture.Tuning, default) { MaxDropMetres = 6 };

    private sealed class NeverReadStream : MemoryStream
    {
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Expectation read the stream.");
    }
}
