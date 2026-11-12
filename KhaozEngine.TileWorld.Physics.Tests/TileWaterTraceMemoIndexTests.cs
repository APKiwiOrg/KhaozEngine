using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterTraceMemoIndexTests
{
    [Fact]
    public void CollisionsSurviveHeadAndMiddleSlotReplacementWithoutChangingFifoStorage()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        var observed = WaterTraceMemoComparison.Read(field, query, bypass: true);
        Assert.Equal(MovementAvailability.Known, observed.Result.Availability);
        var basis = new TileWaterTraceMemo.Key(query, Vector3.Zero, 64, 256, 0);
        var byBucket = new Dictionary<int, List<TileWaterTraceMemo.Key>>();
        List<TileWaterTraceMemo.Key>? collision = null;
        // 513 distinct integer keys in 256 buckets guarantee one bucket with three members.
        // These bits exercise indexing only, with no floating arithmetic or geometry execution.
        for (int i = 0; i <= 512; i++)
        {
            var key = basis with { Dx = i + 1 };
            int bucket = TileWaterTraceMemo.Table.BucketForTests(key);
            if (!byBucket.TryGetValue(bucket, out var keys)) byBucket.Add(bucket, keys = []);
            keys.Add(key);
            if (keys.Count == 3) { collision = keys; break; }
        }
        Assert.NotNull(collision);
        var a = collision[0];
        var b = collision[1];
        var c = collision[2];
        var table = new TileWaterTraceMemo.Table();
        Store(a);
        Store(b);
        for (int i = 2; i < TileWaterTraceMemo.EntryLimit; i++) Store(basis with { Dy = i + 1 });
        Read(a);
        Read(b);
        Store(c); // Reuse slot 0 in the same bucket, replacing its head.
        Assert.False(ReadRaw(a));
        Read(b);
        Read(c);
        var d = basis with { Dz = 999 };
        Store(d); // Reuse slot 1, now a middle link behind the replacement slot 0.
        Assert.False(ReadRaw(b));
        Read(c);
        Read(d);
        Assert.Equal(TileWaterTraceMemo.EntryLimit, table.CountForTests);
        table.Clear();
        Assert.Equal(0, table.CountForTests);
        Assert.False(ReadRaw(c));
        Store(b);
        Read(b);
        Assert.False(ReadRaw(a));

        void Store(TileWaterTraceMemo.Key key) => table.Store(key, observed.Result, observed.Spans, observed.Contacts);
        bool ReadRaw(TileWaterTraceMemo.Key key) => table.TryRead(key, query.Body.CurrentSpace,
            MovementWaterFieldTests.Identity, new MovementCoverageSpan[64], new MovementDomainContact[256], out _);
        void Read(TileWaterTraceMemo.Key key)
        {
            var spans = new MovementCoverageSpan[64];
            var contacts = new MovementDomainContact[256];
            Assert.True(table.TryRead(key, query.Body.CurrentSpace, MovementWaterFieldTests.Identity, spans, contacts, out var result));
            WaterCoverageComparison.Equal(observed.Result, result,
                observed.Spans[..result.SpansWritten], spans[..result.SpansWritten],
                observed.Contacts[..result.ContactsWritten], contacts[..result.ContactsWritten]);
        }
    }
}
