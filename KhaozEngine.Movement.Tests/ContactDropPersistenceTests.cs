using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPersistenceTests(ITestOutputHelper output)
{
    [Fact]
    public void FormatThreeFullFileMatchesGolden()
    {
        using var fixture = new ContactDropFixture();
        var profile = ContactDropIdentityTests.Profile();
        var expected = new NavBakeExpectation(ContactDropFixture.Options, Sources(), [profile]);
        var bake = GroundNavigationBake.Create(fixture.Capture, Sources(), [profile]);
        byte[] normalized = ContactNavigationBaselineTests.NormalizeFile(new(bake, expected));
        string hash = Convert.ToHexStringLower(SHA256.HashData(normalized));
        output.WriteLine(hash);
        Assert.Equal("988d8cc3603cc87ae3f283990cd809f2e18b44edc4fffacc8064121e94874e39", hash);
    }

    [Fact]
    public void FreshAndLoadedDropsAndRewriteBytesMatch()
    {
        using var fixture = new ContactDropFixture();
        var profile = ContactDropIdentityTests.Profile();
        GroundNavigationBake fresh = GroundNavigationBake.Create(fixture.Capture, Sources(), [profile]);
        byte[] bytes = Write(fresh);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        GroundNavigationBake loaded = Load(bytes, new(ContactDropFixture.Options, Sources(), [profile]));
        Assert.Equal(bytes, Write(loaded));
        ContactNavigationFixture.SameGraphBits(fresh.GetProfile("player"), loaded.GetProfile("player"));
        Assert.Contains(loaded.GetProfile("player").Graph.Links, link => link.Kind == NavLinkKind.Drop);
        Assert.Equal(6, loaded.GetProfile("player").MaxDropMetres);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void SerialAndParallelDropBytesAndDiagnosticsMatch(int degree)
    {
        using var fixture = new ContactDropFixture();
        var profile = ContactDropIdentityTests.Profile();
        byte[] serial, parallel;
        SupportQueryMetrics serialMetrics, parallelMetrics;
        using (var measure = SupportQueryCapture.Begin("serial-drops"))
        {
            serial = Write(GroundNavigationBake.Create(fixture.Capture, Sources(), [profile],
                new NavBakeExecution(1) { UseStableContactHoldReuse = true }));
            serialMetrics = measure.Snapshot().Total;
        }
        using (var measure = SupportQueryCapture.Begin("parallel-drops"))
        {
            parallel = Write(GroundNavigationBake.Create(fixture.Capture, Sources(), [profile], new NavBakeExecution(degree)));
            parallelMetrics = measure.Snapshot().Total;
        }
        Assert.Equal(serial, parallel);
        Assert.True(serialMetrics[SupportQueryCounter.DropSteps] > 0);
        foreach (SupportQueryCounter counter in Enum.GetValues<SupportQueryCounter>())
        {
            // Extra worker setup pins and first world-form publication are independent of movement work.
            if (counter is SupportQueryCounter.PhysicsLeases or SupportQueryCounter.MovementLeases or
                SupportQueryCounter.ProducerPins or SupportQueryCounter.WorldFormBuilds) continue;
            Assert.Equal(serialMetrics[counter], parallelMetrics[counter]);
        }
    }

    [Fact]
    public void DisabledMixedProfileRetainsItsGroundIdentity()
    {
        using var fixture = new ContactDropFixture();
        var enabled = ContactDropIdentityTests.Profile();
        var disabled = enabled with { Name = "zero", MaxDropMetres = 0 };
        var legacy = disabled with { Name = "legacy", Tuning = disabled.Tuning with { Controller = MovementControllerKind.Legacy, Water = default } };
        var profiles = new[] { enabled, disabled, legacy };
        byte[] bytes = Write(GroundNavigationBake.Create(fixture.Capture, Sources(), profiles));
        GroundNavigationBake loaded = Load(bytes, new(ContactDropFixture.Options, Sources(), profiles));
        Assert.Equal(bytes, Write(loaded));
        Assert.Equal(0, loaded.GetProfile("zero").MaxDropMetres);
        Assert.Equal(0, loaded.GetProfile("legacy").MaxDropMetres);
        Assert.DoesNotContain(loaded.GetProfile("zero").Space.Links, link => link.Kind == NavLinkKind.Drop);
    }

    [Theory]
    [InlineData("impossible-count")]
    [InlineData("wrong-small-count")]
    [InlineData("truncated-bits")]
    [InlineData("trailing-bit")]
    [InlineData("trailing-byte")]
    public void MalformedAcceptanceNeverPublishes(string fault)
    {
        using var fixture = new ContactDropFixture();
        var profile = ContactDropIdentityTests.Profile();
        var nav = fixture.Capture.BuildProfile(profile.Tuning, profile.Areas,
            new GroundProfileOptions { MaxDropMetres = 6 });
        int count = nav.Space.Links.Count, bitBytes = (count + 7) / 8;
        Assert.NotEqual(0, count);
        byte[] bytes = Write(GroundNavigationBake.Create(fixture.Capture, Sources(), [profile]));
        int start = bytes.Length - bitBytes;
        switch (fault)
        {
            case "impossible-count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start - 4), int.MaxValue); break;
            case "wrong-small-count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start - 4), count - 1); break;
            case "truncated-bits": bytes = bytes[..^1]; break;
            case "trailing-bit":
                Assert.NotEqual(0, count % 8);
                bytes[^1] |= 0x80;
                break;
            case "trailing-byte": bytes = [.. bytes, 0]; break;
        }
        Reseal(bytes);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(new MemoryStream(bytes),
            new(ContactDropFixture.Options, Sources(), [profile])).Status);
    }

    [Fact]
    public void LiveCompatibilityRetainsFlightInputsOnlyForEnabledProfiles()
    {
        using var fixture = new ContactDropFixture();
        GroundNavigation enabled = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 });
        GroundNavigation disabled = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        foreach (MoveTuning changed in new[]
        {
            ContactDropFixture.Tuning with { WalkSpeed = 4 },
            ContactDropFixture.Tuning with { RunSpeed = 6 },
            ContactDropFixture.Tuning with { AirMomentum = !ContactDropFixture.Tuning.AirMomentum },
        })
        {
            Assert.Throws<ArgumentException>(() => enabled.Validate(changed));
            disabled.Validate(changed);
        }
    }
}
