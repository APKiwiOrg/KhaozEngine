using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterCapacityTests
{
    static TileWaterColumn Column(int id, Vector3 min, Vector3 max) => new(new("water-world", $"body/{id}"), min, max,
        $"{id}/min-x", $"{id}/max-x", $"{id}/min-z", $"{id}/max-z", $"{id}/bed", $"{id}/surface");

    [Theory]
    [InlineData(256, MovementAvailability.Known)]
    [InlineData(257, MovementAvailability.CapacityExceeded)]
    public void ExactContactCapAndOneBeyond(int count, MovementAvailability expected)
    {
        var columns = Enumerable.Range(0, count).Select(i => Column(i, new(-10), new(10))).ToArray();
        var query = new MovementMediumSweepQuery(new(Vector3.Zero, 0.125f, 0.25f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = TileWaterColumnCoverage.Trace(columns, query, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(expected, result.Availability);
        if (expected == MovementAvailability.Known) Assert.Equal(256, result.ContactsWritten);
        else
        {
            Assert.Equal(0, result.ContactsWritten);
            Assert.Equal(0, result.SpansWritten);
            Assert.All(spans, s => Assert.Equal(default, s));
            Assert.All(contacts, c => Assert.Equal(default, c));
        }
    }

    [Theory]
    [InlineData(false, MovementAvailability.Known)]
    [InlineData(true, MovementAvailability.CapacityExceeded)]
    public void FaceMultiplicityCountsTowardTheContactCap(bool oneMore, MovementAvailability expected)
    {
        // 43 boxes touch six faces each. The final adjacent pair shares one domain,
        // so its two internal faces are suppressed: 43 * 6 - 2 = 256.
        var columns = Enumerable.Range(0, oneMore ? 44 : 43).Select(i =>
        {
            float x = i == 42 ? 166 : 4 * i;
            var column = Column(i, new(x, -1, 0), new(x + 2, 1, 2));
            return i == 42 ? column with { Domain = new("water-world", "body/41") } : column;
        }).ToArray();
        var query = new MovementMediumSweepQuery(new(Vector3.Zero, 200, 200, MovementWaterFieldTests.Space, null), Vector3.Zero);
        var result = TileWaterColumnCoverage.Trace(columns, query, MovementWaterFieldTests.Identity, new MovementCoverageSpan[64], new MovementDomainContact[256]);
        Assert.Equal(expected, result.Availability);
        Assert.Equal(oneMore ? 0 : 256, result.ContactsWritten);
        Assert.Equal(oneMore ? 257 : 256, result.RequiredContactCapacity);
    }

    [Theory]
    [InlineData(16, MovementAvailability.Known, 64)]
    [InlineData(17, MovementAvailability.CapacityExceeded, 65)]
    public void ExactSpanCapAndOneBeyond(int count, MovementAvailability expected, int required)
    {
        // Four events per pond: entry, loss of the entry face, acquisition of the exit face,
        // and exit. Starting on the first face omits its entry event, leaving 63 cuts.
        // The extra pond adds only its entry event before the end of the path.
        var columns = Enumerable.Range(0, count).Select(i =>
            Column(i, new(i == 16 ? 127.875f : 2 * i, -10, -10), new(i == 16 ? 129 : 2 * i + 1, 10, 10))).ToArray();
        var query = new MovementMediumSweepQuery(new(new(0, 0, 0), 0.125f, 0.25f, MovementWaterFieldTests.Space, null), new(128, 0, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = TileWaterColumnCoverage.Trace(columns, query, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(expected, result.Availability);
        Assert.Equal(required, result.RequiredSpanCapacity);
        if (expected != MovementAvailability.Known)
        {
            Assert.Equal(0, result.ContactsWritten);
            Assert.Equal(0, result.SpansWritten);
            Assert.All(spans, s => Assert.Equal(default, s));
            Assert.All(contacts, c => Assert.Equal(default, c));
        }
    }
}
