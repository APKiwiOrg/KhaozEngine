using System;
using System.Runtime.InteropServices;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterCoverageComparison
{
    internal static void Equal(MovementCoverageResult expected, MovementCoverageResult actual,
        MovementCoverageSpan[] expectedSpans, MovementCoverageSpan[] actualSpans,
        MovementDomainContact[] expectedContacts, MovementDomainContact[] actualContacts)
    {
        Assert.Equal(expected, actual);
        Bits(expected.CertifiedErrorMetres, actual.CertifiedErrorMetres);
        Assert.Same(expected.Identity.ClosureId, actual.Identity.ClosureId);
        Assert.Same(expected.Identity.ScopeDigest, actual.Identity.ScopeDigest);
        Assert.Equal(expectedSpans, actualSpans);
        Assert.Equal(expectedContacts, actualContacts);
        Assert.Equal(MemoryMarshal.AsBytes(expectedSpans.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(actualSpans.AsSpan()).ToArray());
        for (int i = 0; i < expectedSpans.Length; i++) Span(expectedSpans[i], actualSpans[i]);
        for (int i = 0; i < expectedContacts.Length; i++) Contact(expectedContacts[i], actualContacts[i]);
    }

    internal static void Span(MovementCoverageSpan a, MovementCoverageSpan b)
    {
        Assert.Equal(a, b);
        Bits(a.EnterFraction, b.EnterFraction);
        Bits(a.ExitFraction, b.ExitFraction);
    }

    internal static void Contact(MovementDomainContact a, MovementDomainContact b)
    {
        Assert.Equal(a, b);
        Assert.Same(a.Domain.WorldId, b.Domain.WorldId);
        Assert.Same(a.Domain.LocalId, b.Domain.LocalId);
        Assert.Same(a.Space.WorldId, b.Space.WorldId);
        Assert.Same(a.Space.LocalId, b.Space.LocalId);
        Assert.Same(a.Interval.LowerBoundaryId, b.Interval.LowerBoundaryId);
        Assert.Same(a.Interval.UpperBoundaryId, b.Interval.UpperBoundaryId);
        // The frozen evaluator constructs joined-triangle IDs on every trace, so compare their values.
        if (a.BoundaryId?.EndsWith("/joined-triangle", StringComparison.Ordinal) != true)
            Assert.Same(a.BoundaryId, b.BoundaryId);
        Bits(a.Interval.LowerY, b.Interval.LowerY);
        Bits(a.Interval.UpperY, b.Interval.UpperY);
        Bits(a.Interval.NominalSurfaceY, b.Interval.NominalSurfaceY);
        Bits(a.Fraction, b.Fraction);
        Bits(a.IntervalColumnXZ.X, b.IntervalColumnXZ.X);
        Bits(a.IntervalColumnXZ.Y, b.IntervalColumnXZ.Y);
        Bits(a.Normal.X, b.Normal.X);
        Bits(a.Normal.Y, b.Normal.Y);
        Bits(a.Normal.Z, b.Normal.Z);
    }

    static void Bits(float expected, float actual) =>
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
}
