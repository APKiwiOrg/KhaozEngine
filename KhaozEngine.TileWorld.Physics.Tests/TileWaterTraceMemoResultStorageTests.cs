using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterTraceMemoResultStorageTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(64, 256, true)]
    [InlineData(65, 256, false)]
    [InlineData(64, 257, false)]
    public void RetentionLimitsBoundResultsRatherThanCallerCapacity(int spanCount, int contactCount, bool retained)
    {
        var key = Key() with { Spans = 65, Contacts = 257 };
        var spans = Enumerable.Repeat(FrozenWaterComparison.SpanSentinel, spanCount).ToArray();
        var contacts = Enumerable.Repeat(FrozenWaterComparison.ContactSentinel, contactCount).ToArray();
        var result = new MovementCoverageResult(MovementAvailability.Known, spanCount, spanCount,
            contactCount, contactCount, 0, MovementWaterFieldTests.Identity);
        var table = new TileWaterTraceResultMemo.Table();
        table.Store(key, result, spans, contacts);
        var actualSpans = new MovementCoverageSpan[65];
        var actualContacts = new MovementDomainContact[257];
        Assert.Equal(retained, table.TryRead(key, key.Space, result.Identity, actualSpans, actualContacts, out var actual));
        if (!retained) return;
        Assert.Equal(result, actual);
        Assert.Equal(spans, actualSpans[..spanCount]);
        // Replay always replaces the contact space with the validated caller's space.
        Assert.All(actualContacts[..contactCount], c => Assert.Equal(key.Space, c.Space));
    }

    [Fact]
    public void CollisionsAndNinthInsertionKeepCompleteEqualityAndFifoAge()
    {
        var table = new TileWaterTraceResultMemo.Table();
        var buckets = new Dictionary<int, List<TileWaterTraceMemo.Key>>();
        List<TileWaterTraceMemo.Key>? collision = null;
        for (int i = 0; i < 33; i++)
        {
            var key = Key() with { Dx = i + 1 };
            int bucket = TileWaterTraceResultMemo.Table.BucketForTests(key);
            if (!buckets.TryGetValue(bucket, out var keys)) buckets.Add(bucket, keys = []);
            keys.Add(key);
            if (keys.Count == 3) { collision = keys; break; }
        }
        Assert.NotNull(collision);
        var a = collision[0]; var b = collision[1]; var c = collision[2];
        Store(a); Store(b);
        for (int i = 2; i < 8; i++) Store(Key() with { Dy = i + 1 });
        Assert.True(Read(a));
        Store(c);
        Assert.False(Read(a)); Assert.True(Read(b)); Assert.True(Read(c));
        Store(Key() with { Dz = 99 });
        Assert.False(Read(b)); Assert.True(Read(c));
        Assert.Equal(8, table.CountForTests);
        table.Clear();
        Assert.Equal(0, table.CountForTests);
        Assert.Equal(0, table.IdentifierBytes);
        Assert.False(Read(c));
        void Store(TileWaterTraceMemo.Key key) => table.Store(key,
            new(MovementAvailability.CapacityExceeded, 0, 65, 0, 257, 0, MovementWaterFieldTests.Identity), [], []);
        bool Read(TileWaterTraceMemo.Key key) => table.TryRead(key, key.Space, MovementWaterFieldTests.Identity, [], [], out _);
    }

    [Fact]
    public void OwnedArraysAndRetainedIdentifiersHaveIndependentEnforcedBudgets()
    {
        _ = new TileWaterTraceResultMemo.Table(); // Warm constructor/JIT before measuring one table.
        TileWaterTraceResultMemo.Table? table = null;
        long allocated = AllocationMeasurement.Measure("one exact-result table", () => table = new());
        Assert.NotNull(table);
        Assert.InRange(allocated, 239616, TileWaterTraceResultMemo.ByteLimit);
        Assert.InRange(table.OwnedBytes, allocated, TileWaterTraceResultMemo.ByteLimit);
        var key = Key();
        var result = new MovementCoverageResult(MovementAvailability.CapacityExceeded, 0, 65, 0, 257, 0, MovementWaterFieldTests.Identity);
        table.Store(key, result, [], []);
        Assert.True(Read(table));
        Assert.InRange(table.IdentifierBytes, 1, TileWaterTraceResultMemo.IdentifierByteLimit);
        var tooSmall = new TileWaterTraceResultMemo.Table(byteBudget: table.OwnedBytes - 1);
        tooSmall.Store(key, result, [], []);
        Assert.False(Read(tooSmall));
        var noStrings = new TileWaterTraceResultMemo.Table(identifierBudget: 0);
        noStrings.Store(key, result, [], []);
        Assert.False(Read(noStrings));
        output.WriteLine($"RESULT_TIER_MEMORY allocated={allocated}, owned-charge={table.OwnedBytes}, identifier-charge={table.IdentifierBytes}, key={Unsafe.SizeOf<TileWaterTraceMemo.Key>()}, span={Unsafe.SizeOf<MovementCoverageSpan>()}, contact={Unsafe.SizeOf<MovementDomainContact>()}");
        bool Read(TileWaterTraceResultMemo.Table value) => value.TryRead(key, key.Space, result.Identity, [], [], out _);
    }

    [Theory]
    [InlineData(MovementAvailability.Stale)]
    [InlineData(MovementAvailability.Invalid)]
    public void ProducerAndLifetimeRefusalsCannotBeRetained(MovementAvailability status)
    {
        var table = new TileWaterTraceResultMemo.Table();
        var key = Key();
        table.Store(key, new(status, 0, 0, 0, 0, 0, MovementWaterFieldTests.Identity), [], []);
        Assert.False(table.TryRead(key, key.Space, MovementWaterFieldTests.Identity, [], [], out _));
    }

    internal static TileWaterTraceMemo.Key Key() => new(TileWaterTraceMemoResultTests.Query(), Vector3.Zero, 64, 256, 0);
}
