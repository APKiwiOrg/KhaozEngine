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
