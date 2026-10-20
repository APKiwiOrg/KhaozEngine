using System;
using System.Collections.Generic;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseKeyTests(ITestOutputHelper output)
{
    public static TheoryData<string> NearMisses() => SupportFootprintProofReuseFixture.NearMisses();

    [Theory]
    [MemberData(nameof(NearMisses))]
    public void OneBitKeyDifferenceMisses(string component)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var (request, filter, certification) = SupportFootprintProofReuseFixture.NearMiss(component, fixture.Request);
        var key = SupportFootprintProofKey.Create(fixture.Request, default, false);
        Assert.NotEqual(key, SupportFootprintProofKey.Create(request, filter, certification));
        fixture.FillSentinels();
        var expectedMembers = (SupportFootprintMember[])fixture.Members.Clone();
        var expectedJoins = (ulong[])fixture.Joins.Clone();
        SupportFootprintResult expected = default;
        Exception? expectedError = Invoke(() => expected = Query(expectedMembers, expectedJoins));
        if (component is "filter.layers" or "filter.invalid-mobility") Assert.IsType<ArgumentException>(expectedError);
        else
        {
            Assert.Null(expectedError);
            bool refused = component.StartsWith("contact-band.", StringComparison.Ordinal) || component.Contains(".q", StringComparison.Ordinal);
            Assert.Equal(refused ? CapsuleFeatureStatus.Unsupported : CapsuleFeatureStatus.Complete, expected.Status);
        }
        using var session = fixture.Open(lease);
        Assert.Equal(CapsuleFeatureStatus.Complete, fixture.Query(lease).Status);
        fixture.FillSentinels();
        SupportQueryCounts before = SupportQueryDiagnostics.Snapshot();
        SupportFootprintResult actual = default;
        Exception? error = Invoke(() => actual = Query(fixture.Members, fixture.Joins));
        SupportQueryCounts delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(expectedError?.GetType(), error?.GetType());
        if (error is null)
        {
            Assert.Equal(SupportFootprintProofReuseFixture.SerializePayloadBits(expected, expectedMembers, expectedJoins),
                SupportFootprintProofReuseFixture.SerializePayloadBits(actual, fixture.Members, fixture.Joins));
            Assert.Equal(1, delta.FootprintReuseMisses);
            if (actual.Status == CapsuleFeatureStatus.Complete)
            {
                Assert.Equal(1, delta.Volumes);
                Assert.Equal(1, delta.FootprintReuseStores);
            }
            else Assert.Equal(0, delta.FootprintReuseStores);
        }
        else Assert.Equal(0, delta.FootprintReuseStores);
        Assert.Equal(expectedMembers, fixture.Members);
        Assert.Equal(expectedJoins, fixture.Joins);
        output.WriteLine($"{component}: hits={delta.FootprintReuseHits}, misses={delta.FootprintReuseMisses}, " +
            $"volumes={delta.Volumes}, stores={delta.FootprintReuseStores}, status={actual.Status}, exception={error?.GetType().Name ?? "none"}");

        SupportFootprintResult Query(SupportFootprintMember[] members, ulong[] joins)
        {
            if (!certification) return fixture.World.QuerySupportFootprint(lease, request, members, joins, filter);
            using (SupportCertificationJoins.Arm()) return fixture.World.QuerySupportFootprint(lease, request, members, joins, filter);
        }
    }

    [Fact]
    public void HashCollisionRequiresFullEquality()
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        var first = fixture.Query(lease);
        byte[] expected = SupportFootprintProofReuseFixture.SerializePayloadBits(first, fixture.Members, fixture.Joins);
        var key = SupportFootprintProofKey.Create(fixture.Request, default, false);
        var other = key with { AxisX = key.AxisX + 1 };
        var comparer = new CollidingComparer();
        Assert.Equal(comparer.GetHashCode(key), comparer.GetHashCode(other));
        var collided = new Dictionary<SupportFootprintProofKey, int>(comparer) { [key] = 1, [other] = 2 };
        Assert.Equal(2, collided.Count);
        Assert.Equal(1, collided[key]);
        Assert.Equal(2, collided[other]);
        // Every key hashes to the same production table slot at capacity one.
        using var memo = new SupportFootprintProofMemo(1);
        memo.Remember(key, first, fixture.Members, fixture.Joins);
        fixture.FillSentinels();
        var members = (SupportFootprintMember[])fixture.Members.Clone();
        var joins = (ulong[])fixture.Joins.Clone();
        Assert.False(memo.TryCopy(fixture.World, lease, other, fixture.Members, fixture.Joins, out _));
        Assert.Equal(members, fixture.Members);
        Assert.Equal(joins, fixture.Joins);
        Assert.True(memo.TryCopy(fixture.World, lease, key, fixture.Members, fixture.Joins, out var copied));
        Assert.Equal(expected, SupportFootprintProofReuseFixture.SerializePayloadBits(copied, fixture.Members, fixture.Joins));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProfileAndSourceTokenChangesResetAllPayloads(bool changeProfile)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease);
        var first = fixture.Query(lease);
        var expected = fixture.Bits(first);
        Assert.Equal(1, SupportQueryDiagnostics.Retention.Keys);
        var domain = new SupportFootprintReuseDomain(changeProfile ? new object() : fixture.Domain.ProfileIdentity,
            changeProfile ? fixture.Domain.SourceIdentity : new object());
        session.ResetSource(domain);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
        var before = SupportQueryDiagnostics.Snapshot();
        var actual = fixture.Query(lease);
        Assert.Equal(expected, fixture.Bits(actual));
        var delta = SupportQueryDiagnostics.Snapshot() - before;
        Assert.Equal(0, delta.FootprintReuseHits);
        Assert.Equal(1, delta.FootprintReuseMisses);
        Assert.Equal(1, delta.Volumes);
    }

    static Exception? Invoke(Action query)
    {
        try { query(); return null; }
        catch (Exception error) { return error; }
    }

    sealed class CollidingComparer : IEqualityComparer<SupportFootprintProofKey>
    {
        public bool Equals(SupportFootprintProofKey x, SupportFootprintProofKey y) => x.Equals(y);
        public int GetHashCode(SupportFootprintProofKey obj) => 0;
    }
}
