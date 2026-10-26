using System;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

[Collection("AllocSensitive")]
public sealed class MapFoliageInstanceBatchTestsLayout
{
    [Fact]
    public void NativeControlsAndBuilderRows_MatchPinned64BitLayouts()
    {
        Assert.Equal(96, Unsafe.SizeOf<MapFoliageInstanceBuilder.Row>());
        (Type Type, long Bytes)[] layouts =
        [
            (typeof(GroundCoverInstanceColumns), 56),
            (typeof(MapFoliageInstanceBatch), 104),
            (typeof(MapFoliageWork), 80),
            (typeof(MapFoliageColumnBudget), 40),
            (typeof(MapFoliageOrdinalTable), 48),
            (typeof(MapFoliageInstanceBuilder), 104),
        ];
        foreach (var layout in layouts)
        {
            object held = RuntimeHelpers.GetUninitializedObject(layout.Type);
            long actual = AllocationMeasurement.Measure("native foliage control " + layout.Type.Name,
                () => held = RuntimeHelpers.GetUninitializedObject(layout.Type));
            GC.KeepAlive(held);
            Assert.Equal(layout.Bytes, actual);
        }
    }
}
