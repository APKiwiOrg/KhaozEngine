using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterSeamComparison
{
    internal readonly record struct Result(MovementCoverageResult Before, MovementCoverageResult After,
        int OldSolves, int NewSolves, int Proofs, int Certificates);

    internal static Result Check(TileMediumSampler field, TileWaterTraceTestSnapshot legacy,
        MovementMediumSweepQuery query, Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null)
    {
        var expectedSpans = new MovementCoverageSpan[64];
        var actualSpans = new MovementCoverageSpan[64];
        var expectedContacts = new MovementDomainContact[256];
        var actualContacts = new MovementDomainContact[256];
        var token = identity ?? MovementWaterFieldTests.Identity;
        TileWaterQueryWork.Reset();
        try
        {
            var before = WaterTraceReference.Trace(legacy, query, origin, token,
                expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
            int oldSolves = TileWaterQueryWork.ConvexIntersections;
            TileWaterQueryWork.Reset();
            var after = field.TraceWater(query, origin, token,
                actualSpans.AsSpan(0, spanCapacity), actualContacts.AsSpan(0, contactCapacity));
            var result = new Result(before, after, oldSolves, TileWaterQueryWork.ConvexIntersections,
                TileWaterQueryWork.BedSeamProofs, TileWaterQueryWork.BedSeamCertificates);
            if (before.Availability == MovementAvailability.PrecisionExceeded && after.Availability == MovementAvailability.Known)
            {
                Assert.True(result.Certificates > 0);
                Assert.InRange(after.CertifiedErrorMetres, 0, MovementQueryLease.CoverageSkinMetres);
                Assert.True(after.ContactsWritten > 0);
                Assert.Equal(token, after.Identity);
                Assert.All(actualContacts.Take(after.ContactsWritten), contact => Assert.Equal(query.Body.CurrentSpace, contact.Space));
                return result;
            }
            // A legacy Known result has no over-budget feature charge. Its output and certificate
            // stay bit-identical, as do all refusals that the continuous seam cannot discharge.
            WaterCoverageComparison.Equal(before, after, expectedSpans, actualSpans, expectedContacts, actualContacts);
            Assert.Equal(oldSolves, result.NewSolves);
            return result;
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }
}
