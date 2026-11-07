using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class PlayerPublicationIdentityTests
{
    [Theory]
    [InlineData(0f, false)]
    [InlineData(6f, false)]
    [InlineData(6f, true)]
    public void EarlierPublicationPolicyDecodesButRefusesAtTheSameEngineVersion(float drop, bool terrain)
    {
        // #1707. Frozen formats keep decoding. Their old proof trajectory cannot satisfy the new policy identity.
        var options = ContactDropFixture.Options with { CaptureTerrainDescentEvidence = terrain };
        var expected = new NavBakeExpectation(options, ContactBakeFile.Sources(),
            [new NavBakeProfile("player", ContactDropFixture.Tuning, default) { MaxDropMetres = drop, AllowTerrainDescents = terrain }]);
        ushort format = NavBakeIdentity.FormatFor(options, expected.Profiles);
        byte[] current = NavBakeIdentity.Encode(expected, "controlled-engine");
        byte[] earlier = WithoutPublicationSource(current, terrain);
        Assert.True(NavBakeIdentity.TryDecode(earlier, out var decoded, out _, format));
        Assert.Equal("controlled-engine", decoded.EngineVersion);
        var result = NavBakeIdentity.Compare(earlier, current, format, format);
        Assert.Equal(NavBakeLoadStatus.SourcesChanged, result.Status);
        Assert.Contains(NavBakeIdentity.PlayerPublicationSource, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyOnlyIdentityHasNoPublicationSourceAndContactReservesItsLabel()
    {
        var expected = new NavBakeExpectation(ContactDropFixture.Options, ContactBakeFile.Sources(),
            [new NavBakeProfile("legacy", MoveTuning.Default, default)]);
        byte[] identity = NavBakeIdentity.Encode(expected, "controlled-engine");
        Assert.True(NavBakeIdentity.TryDecode(identity, out var decoded, out _, 1));
        Assert.DoesNotContain(decoded.Sources, source => source.Label == NavBakeIdentity.PlayerPublicationSource);
        var reserved = new NavBakeSources().Add(NavBakeIdentity.PlayerPublicationSource, new byte[32]);
        Assert.Throws<ArgumentException>(() => NavBakeIdentity.Encode(expected with
        {
            Sources = reserved,
            Profiles = [new("contact", ContactDropFixture.Tuning, default)]
        }, "controlled-engine"));
    }

    static byte[] WithoutPublicationSource(byte[] identity, bool terrain)
    {
        int countAt = 2 + BinaryPrimitives.ReadUInt16LittleEndian(identity) + 13 * 4 + 1 + (terrain ? 1 : 0);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(identity.AsSpan(countAt));
        int at = countAt + 2;
        for (int i = 0; i < count; i++)
        {
            int length = 1 + identity[at] + 32;
            if (Encoding.ASCII.GetString(identity, at + 1, identity[at]) == NavBakeIdentity.PlayerPublicationSource)
            {
                byte[] old = identity[..at].Concat(identity[(at + length)..]).ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(old.AsSpan(countAt), (ushort)(count - 1));
                return old;
            }
            at += length;
        }
        throw new InvalidOperationException("The Contact identity did not bind publication.");
    }
}
