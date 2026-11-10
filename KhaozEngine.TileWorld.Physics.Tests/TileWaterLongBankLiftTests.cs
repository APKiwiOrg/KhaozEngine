using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterLongBankLiftTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(110.275390625f, -0.36105501651763916f, -236.69903564453125f, 0.2f)]
    [InlineData(110.275390625f, -0.36105501651763916f, -236.69903564453125f, 0.4f)]
    [InlineData(110.375f, -0.4560607671737671f, -236.79904174804688f, 0.2f)]
    [InlineData(110.375f, -0.4560607671737671f, -236.79904174804688f, 0.4f)]
    [InlineData(111.275390625f, -0.36105501651763916f, -236.69903564453125f, 0.2f)]
    [InlineData(111.275390625f, -0.36105501651763916f, -236.69903564453125f, 0.4f)]
    [InlineData(111.375f, -0.4560607671737671f, -236.79904174804688f, 0.2f)]
    [InlineData(111.375f, -0.4560607671737671f, -236.79904174804688f, 0.4f)]
    public void ALongLegalLandingLiftCertifiesItsInternalDiagonal(float x, float y, float z, float lift)
    {
        // #1761 r1. The union need only discharge the uncertain diagonal crossing, while
        // the later real bed exit retains its own certified root and dry coverage.
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));
        var query = new MovementMediumSweepQuery(new(new(x, y, z), 0.3f, 0.75f, GrazingWaterBank.Space, null), new(0, lift, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        TileWaterQueryWork.Reset();
        MovementCoverageResult result;
        try
        {
            result = field.TraceWater(query, Vector3.Zero, GrazingWaterBank.Identity, spans, contacts);
            output.WriteLine($"{result.Availability}, proofs={TileWaterQueryWork.BedSeamProofs}, certificates={TileWaterQueryWork.BedSeamCertificates}, error={result.CertifiedErrorMetres:R}");
            Assert.True(TileWaterQueryWork.BedSeamProofs > 0);
        }
        finally { TileWaterQueryWork.Enabled = false; }
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.InRange(result.CertifiedErrorMetres, 0, MovementQueryLease.CoverageSkinMetres);
        Assert.Equal(0, spans[0].EnterFraction);
        Assert.Equal(1, spans[result.SpansWritten - 1].ExitFraction);
        Assert.True(result.ContactsWritten > 0);
        Assert.All(contacts[..result.ContactsWritten], contact => Assert.Equal(GrazingWaterBank.Space, contact.Space));
    }
}
