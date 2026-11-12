using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterTraceMemoTests
{
    [Theory]
    [InlineData(.096971154f, false)]
    [InlineData(.4f, true)]
    public void ARepeatedPrismTraceReturnsItsOwnedOutputWithoutExecutingTheKernel(float lift, bool multipleSpans)
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = new MovementMediumSweepQuery(GrazingWaterBank.Lift(110.5f).Body, new(0, lift, 0));
        // #1755. Preserve every public bit and untouched caller suffix while avoiding real work.
        var cold = WaterTraceMemoComparison.Check(field, query);
        WaterTraceMemoComparison.Memo(cold, hit: false);
        Assert.Equal(MovementAvailability.Known, cold.Result.Availability);
        Assert.Equal(multipleSpans, cold.Result.SpansWritten > 1);
        Assert.True(cold.Result.ContactsWritten > 1);
        Assert.True(cold.Solves > 0);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: true);
        field.ResetTraceMemoForTests();
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: false);
    }

    [Fact]
    public void IdentityAndSupportValidationRunBeforeAHit()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        WaterTraceMemoComparison.Check(field, query);
        var identity = new MovementQueryIdentity("another-closure", 7, "another-scope");
        var warm = WaterTraceMemoComparison.Check(field, query, identity: identity);
        WaterTraceMemoComparison.Memo(warm, hit: true);
        Assert.Equal(identity, warm.Result.Identity);
        var body = query.Body;
        var supported = new MovementMediumSweepQuery(new(body.Centre, body.Radius, body.HalfHeight,
            body.CurrentSpace, new(body.CurrentSpace.WorldId, "valid-support")), query.Delta);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, supported), hit: true);
        var invalid = WaterTraceMemoComparison.Check(field, query, identity: default(MovementQueryIdentity));
        Assert.Equal(MovementAvailability.Invalid, invalid.Result.Availability);
        Assert.Equal(0, invalid.Logical);
        Assert.Equal(0, invalid.Hits);
        Assert.Equal(0, invalid.Solves);
        var foreign = new MovementMediumSweepQuery(new(body.Centre, body.Radius, body.HalfHeight,
            new("another-world", body.CurrentSpace.LocalId), null), query.Delta);
        Assert.Equal(MovementAvailability.Invalid, WaterTraceMemoComparison.Check(field, foreign).Result.Availability);
        Assert.Equal(MovementAvailability.Invalid, WaterTraceMemoComparison.Check(field, default).Result.Availability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void ChangingOneQueryInputCannotReuseTheOldEntry(int component)
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        WaterTraceMemoComparison.Check(field, query);
        Vector3 centre = query.Body.Centre, delta = query.Delta, origin = Vector3.Zero;
        float radius = query.Body.Radius, height = query.Body.HalfHeight;
        var space = query.Body.CurrentSpace;
        int spans = 64, contacts = 256;
        switch (component)
        {
            case 0: centre.X = MathF.BitIncrement(centre.X); break;
            case 1: centre.Y = MathF.BitIncrement(centre.Y); break;
            case 2: centre.Z = MathF.BitIncrement(centre.Z); break;
            case 3: delta.X = .00001f; break;
            case 4: delta.Y = MathF.BitIncrement(delta.Y); break;
            case 5: delta.Z = .00001f; break;
            case 6: radius = MathF.BitIncrement(radius); break;
            case 7: height = MathF.BitIncrement(height); break;
            case 8: origin.X = .00001f; break;
            case 9: origin.Y = .00001f; break;
            case 10: origin.Z = .00001f; break;
            case 11: space = new(space.WorldId, "another-space"); break;
            case 12: spans--; break;
            case 13: contacts--; break;
        }
        var changed = new MovementMediumSweepQuery(new(centre, radius, height, space, null), delta);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, changed, origin, spans, contacts), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, changed, origin, spans, contacts), hit: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void SignedZeroRemainsPartOfTheQuery(int component)
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var body = GrazingWaterBank.Lift(110.5f).Body;
        Vector3 delta = Vector3.Zero, origin = Vector3.Zero;
        if (component >= 6)
        {
            origin = body.Centre;
            body = new(Vector3.Zero, body.Radius, body.HalfHeight, body.CurrentSpace, null);
        }
        WaterTraceMemoComparison.Check(field, new(body, Vector3.Zero), origin);
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        if (component < 3) delta[component] = negativeZero;
        else if (component < 6) origin[component - 3] = negativeZero;
        else
        {
            var centre = body.Centre;
            centre[component - 6] = negativeZero;
            body = new(centre, body.Radius, body.HalfHeight, body.CurrentSpace, null);
        }
        var changed = new MovementMediumSweepQuery(body, delta);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, changed, origin), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, changed, origin), hit: true);
    }

    [Fact]
    public void EqualAbsolutePathsStillKeyTheUncombinedOriginAndCentreBits()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        WaterTraceMemoComparison.Check(field, query);
        Vector3 origin = new(16, 8, -32);
        var translated = new MovementMediumSweepQuery(new(query.Body.Centre - origin, query.Body.Radius,
            query.Body.HalfHeight, query.Body.CurrentSpace, null), query.Delta);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, translated, origin), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, translated, origin), hit: true);
    }

    [Fact]
    public void FlatPathsAndPointQueriesDoNotEnterThePrismMemo()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var dry = new MovementMediumSweepQuery(new(new(1000, -1, 1000), .3f, .75f,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        Assert.Equal(0, WaterTraceMemoComparison.Check(field, dry).Logical);
        Assert.Equal(0, WaterTraceMemoComparison.Check(field, dry).Logical);
        TileWaterQueryWork.Reset();
        try
        {
            Assert.Equal(MovementAvailability.Known,
                field.SampleCentreWater(GrazingWaterBank.Lift().Body, Vector3.Zero).Availability);
            Assert.Equal(0, TileWaterQueryWork.PrismTraces);
            Assert.Equal(0, TileWaterQueryWork.MemoHits);
            Assert.Equal(0, TileWaterQueryWork.MemoMisses);
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

    [Fact]
    public void MemoCountersStayDisabledUnlessTheCallerOptsIn()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        TileWaterQueryWork.Reset();
        TileWaterQueryWork.Enabled = false;
        var cold = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        var warm = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, warm.Availability);
        Assert.Equal(cold, warm);
        Assert.Equal(0, TileWaterQueryWork.PrismTraces);
        Assert.Equal(0, TileWaterQueryWork.MemoHits);
        Assert.Equal(0, TileWaterQueryWork.MemoMisses);
    }
}
