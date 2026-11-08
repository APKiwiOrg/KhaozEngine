using System;
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
        Assert.Equal(expectedSpans, actualSpans);
        Assert.Equal(expectedContacts, actualContacts);
        for (int i = 0; i < expected.SpansWritten; i++)
        {
            Bits(expectedSpans[i].EnterFraction, actualSpans[i].EnterFraction);
            Bits(expectedSpans[i].ExitFraction, actualSpans[i].ExitFraction);
        }
        for (int i = 0; i < expected.ContactsWritten; i++)
        {
            var a = expectedContacts[i];
            var b = actualContacts[i];
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
    }

    static void Bits(float expected, float actual) =>
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
}
