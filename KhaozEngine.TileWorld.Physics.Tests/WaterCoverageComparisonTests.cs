using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterCoverageComparisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnwrittenElementsRejectSignedZeroInEveryFloatingField(bool frozen)
    {
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        foreach (bool exit in new[] { false, true })
        {
            MovementCoverageSpan[] a = [new(0, 0, 0, 0, true)];
            MovementCoverageSpan[] b = [new(exit ? 0 : negativeZero, exit ? negativeZero : 0, 0, 0, true)];
            Compare(frozen, a, a, [], []);
            Assert.ThrowsAny<XunitException>(() => Compare(frozen, a, b, [], []));
        }
        foreach (string field in new[] { "lower", "upper", "surface", "fraction", "column-x", "column-z", "normal-x", "normal-y", "normal-z" })
        {
            MovementDomainContact[] a = [Contact(field, 0)];
            MovementDomainContact[] b = [Contact(field, negativeZero)];
            Compare(frozen, [], [], a, a);
            Assert.ThrowsAny<XunitException>(() => Compare(frozen, [], [], a, b));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnwrittenRawSpanSentinelsRejectOneChangedNanPayloadBit(bool frozen)
    {
        MovementCoverageSpan[] a = [new(0, 0, 0, 0, true)];
        // The constructor rejects NaN. An unwritten, caller-owned unmanaged buffer can hold arbitrary bits.
        MemoryMarshal.Cast<MovementCoverageSpan, int>(a.AsSpan())[0] = 0x7fc00001;
        Assert.Equal(0x7fc00001, BitConverter.SingleToInt32Bits(a[0].EnterFraction));
        MovementCoverageSpan[] b = (MovementCoverageSpan[])a.Clone();
        Compare(frozen, a, b, [], []);
        MemoryMarshal.Cast<MovementCoverageSpan, int>(b.AsSpan())[0] ^= 1;
        Assert.ThrowsAny<XunitException>(() => Compare(frozen, a, b, [], []));
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("written-spans")]
    [InlineData("required-spans")]
    [InlineData("written-contacts")]
    [InlineData("required-contacts")]
    [InlineData("error")]
    [InlineData("closure")]
    [InlineData("policy")]
    [InlineData("scope")]
    [InlineData("closure-reference")]
    [InlineData("scope-reference")]
    public void ResultAndRefusalIdentifiersCannotBeIgnoredByTheComparator(string field)
    {
        // #1756. Value equality alone misses the error sign bit and current caller references.
        var id = MovementWaterFieldTests.Identity;
        var expected = new MovementCoverageResult(MovementAvailability.Known, 1, 1, 1, 1, 0, id);
        var changedId = new MovementQueryIdentity(
            field == "closure" ? "other" : field == "closure-reference" ? new(id.ClosureId.ToCharArray()) : id.ClosureId,
            field == "policy" ? id.PolicyVersion + 1 : id.PolicyVersion,
            field == "scope" ? "other" : field == "scope-reference" ? new(id.ScopeDigest.ToCharArray()) : id.ScopeDigest);
        bool refusal = field is "availability" or "required-spans" or "required-contacts";
        if (refusal) expected = new(MovementAvailability.CapacityExceeded, 0, 1, 0, 1, 0, id);
        var actual = new MovementCoverageResult(
            field == "availability" ? MovementAvailability.Unresolved : expected.Availability,
            field == "written-spans" ? 2 : expected.SpansWritten,
            field is "written-spans" or "required-spans" ? 2 : expected.RequiredSpanCapacity,
            field == "written-contacts" ? 2 : expected.ContactsWritten,
            field is "written-contacts" or "required-contacts" ? 2 : expected.RequiredContactCapacity,
            field == "error" ? BitConverter.Int32BitsToSingle(int.MinValue) : 0, changedId);
        WaterCoverageComparison.Equal(expected, expected, [], [], [], []);
        Assert.ThrowsAny<XunitException>(() => WaterCoverageComparison.Equal(expected, actual, [], [], [], []));
    }

    [Theory]
    [InlineData("domain-world")]
    [InlineData("domain-local")]
    [InlineData("space-world")]
    [InlineData("space-local")]
    [InlineData("lower-boundary")]
    [InlineData("upper-boundary")]
    [InlineData("boundary")]
    [InlineData("free-surface")]
    [InlineData("region")]
    [InlineData("overlap")]
    [InlineData("span-overlap")]
    public void CallerContactIdentifiersAndClassificationsCannotBeIgnored(string field)
    {
        var a = FrozenWaterComparison.ContactSentinel;
        var b = new MovementDomainContact(
            new(Copy("domain-world", a.Domain.WorldId), Copy("domain-local", a.Domain.LocalId)),
            new(Copy("space-world", a.Space.WorldId), Copy("space-local", a.Space.LocalId)),
            new(a.Interval.LowerY, a.Interval.UpperY, a.Interval.NominalSurfaceY,
                field != "free-surface", Copy("lower-boundary", a.Interval.LowerBoundaryId),
                Copy("upper-boundary", a.Interval.UpperBoundaryId)),
            a.IntervalColumnXZ, a.Normal, a.Fraction, Copy("boundary", a.BoundaryId),
            field == "region" ? a.CoverageRegionHandle + 1 : a.CoverageRegionHandle,
            field == "overlap" ? MovementContactOverlap.Overlapping : a.Overlap,
            field == "span-overlap" ? MovementContactOverlap.Overlapping : a.SpanOverlap);
        Compare(false, [], [], [a], [a]);
        Assert.ThrowsAny<XunitException>(() => Compare(false, [], [], [a], [b]));
        string Copy(string name, string value) => field == name ? new(value.ToCharArray()) : value;
    }

    [Theory]
    [InlineData("start")]
    [InlineData("count")]
    [InlineData("dry")]
    public void CallerSpanClassificationsCannotBeIgnored(string field)
    {
        var a = new MovementCoverageSpan(0, 1, 0, 1, true);
        var b = new MovementCoverageSpan(0, 1, field == "start" ? 1 : 0, field == "count" ? 2 : 1, field != "dry");
        Compare(false, [a], [a], [], []);
        Assert.ThrowsAny<XunitException>(() => Compare(false, [a], [b], [], []));
    }

    static void Compare(bool frozen, MovementCoverageSpan[] a, MovementCoverageSpan[] b,
        MovementDomainContact[] c, MovementDomainContact[] d)
    {
        var result = new MovementCoverageResult(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, MovementWaterFieldTests.Identity);
        if (frozen) FrozenWaterComparison.Equal(result, result, a, b, c, d);
        else WaterCoverageComparison.Equal(result, result, a, b, c, d);
    }

    static MovementDomainContact Contact(string field, float value)
    {
        MovementWaterInterval interval = field switch
        {
            "lower" => new(value, 1, 1, true, "bed", "surface"),
            "upper" => new(-1, value, 1, false, "bed", "ceiling"),
            "surface" => new(-1, 0, value, false, "bed", "ceiling"),
            _ => new(-9, -3, -3, true, "bed", "surface")
        };
        Vector3 normal = field == "normal-x" ? new(value, 1, 0) :
            field == "normal-y" ? new(1, value, 0) : field == "normal-z" ? new(1, 0, value) : Vector3.UnitX;
        return new(new("sentinel", "domain"), new("sentinel", "space"), interval,
            new(field == "column-x" ? value : 17, field == "column-z" ? value : 19), normal,
            field == "fraction" ? value : 0.625f, "boundary", 91,
            MovementContactOverlap.Tangent, MovementContactOverlap.Tangent);
    }
}
