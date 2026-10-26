using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeFoliageRetainedInventoryTests
{
    [Fact]
    public void Growth_ChargesTheCurrentRootAndWindowUntilReleased()
    {
        using var inventory = new MapFoliageRetainedInventory();
        var first = new MapSceneAdmission(new(MaxPreparedBytes: 1024));
        using (first.LimitPreparation(64)) inventory.Grow(64, first, null);
        var current = new MapSceneAdmission(new(MaxPreparedBytes: 1024));
        using var window = current.LimitPreparation(32);
        inventory.Grow(32, current, null);
        Assert.Equal(64, first.RetainedBytes);
        Assert.Equal(32, current.RetainedBytes);
        Assert.Equal(32, Binding(window).Live);
        inventory.ReduceTo(64);
        Assert.Equal(64, first.RetainedBytes);
        Assert.Equal(0, current.RetainedBytes);
        Assert.Equal(0, Binding(window).Live);
        inventory.Dispose();
        Assert.Equal(0, first.RetainedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Growth_RefusesTheCurrentLimitBeforeRetentionAndPreservesEarlierOwnership(bool windowBinds)
    {
        using var inventory = new MapFoliageRetainedInventory();
        var first = new MapSceneAdmission(new(MaxPreparedBytes: 1024));
        using (first.LimitPreparation(1024)) inventory.Grow(64, first, null);
        var current = new MapSceneAdmission(new(MaxPreparedBytes: windowBinds ? 1024 : 0));
        using var window = current.LimitPreparation(windowBinds ? 0 : 1024);
        int allocations = 0;
        Assert.Throws<MapSceneCapacityException>(() => inventory.Grow(32, current, _ =>
        {
            allocations++;
            throw new InvalidOperationException("Allocation must not start before admission.");
        }));
        Assert.Equal(0, allocations);
        Assert.Equal(64, inventory.Bytes);
        Assert.Equal(64, first.RetainedBytes);
        Assert.Equal(0, current.RetainedBytes);
        Assert.Equal(0, Binding(window).Live);
    }

    [Fact]
    public void Growth_RetentionFailureReturnsOnlyTheNewReservation()
    {
        using var inventory = new MapFoliageRetainedInventory();
        var first = new MapSceneAdmission(new(MaxPreparedBytes: 1024));
        inventory.Grow(64, first, null);
        var current = new MapSceneAdmission(new(MaxPreparedBytes: 1024));
        using var window = current.LimitPreparation(128);
        long chargedDuringRetention = -1;
        Assert.Throws<InvalidOperationException>(() => inventory.Grow(32, current, _ =>
        {
            chargedDuringRetention = current.RetainedBytes;
            throw new InvalidOperationException("Synthetic CPU retention refusal.");
        }));
        Assert.Equal(32, chargedDuringRetention);
        Assert.Equal(64, inventory.Bytes);
        Assert.Equal(64, first.RetainedBytes);
        Assert.Equal(0, current.RetainedBytes);
        Assert.Equal(0, Binding(window).Live);
    }

    internal static MapPreparationLedger.Window Binding(MapSceneAdmission.PreparationWindow window) =>
        (MapPreparationLedger.Window)typeof(MapSceneAdmission.PreparationWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(MapPreparationLedger.Window)).GetValue(window)!;
}
