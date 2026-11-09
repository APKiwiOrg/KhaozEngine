using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapDoc.Foliage;

[Collection("AllocSensitive")]
public sealed class MapFoliageInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public void FullSamples_CountSharedObjectsOnceAndPublicMaterialCopiesSeparately()
    {
        string id = new('é', 1);
        var policy = new MapFoliagePolicy(id, 1, 0);
        var materials = Array.AsReadOnly(new[] { id });
        var first = Sample(id, policy, materials);
        var second = Sample(id, policy, materials);
        var work = new MapFoliageWork(0, 2, 0, 0, 0, 0, 0, 0);
        var batch = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, [first, second], work);
        // Batch 64, work 80, wrapper 24, two reference slots in a 40-byte array,
        // two 248-byte samples, one 32-byte policy, one 24-byte UTF-16 string,
        // and one shared 24-byte material wrapper plus its 32-byte array.
        Assert.Equal(816, MapFoliageInventory.FullSamples([batch, batch]));
        var equalText = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id,
            [first, Sample(new string('é', 1), policy, materials)], work);
        Assert.Equal(840, MapFoliageInventory.FullSamples([equalText]));
        var copied = new MapFoliageSample(first.Key, null, null, id, materials, 0, default, Quaternion.Identity, 1, Matrix4x4.Identity, 0);
        var publicBatch = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, [first, copied], work);
        Assert.Equal(872, MapFoliageInventory.FullSamples([publicBatch]));
    }

    [Fact]
    public void NativeAndConstruction_IncludeEveryAlignedArrayWithoutFullSampleRows()
    {
        // Three rows. Matrix 216, position 64, five 40-byte scalar arrays,
        // support table 40 and model table 32. Controls and metadata are separate.
        Assert.Equal(552, MapFoliageInventory.NativeColumns(3, 2, 1));
        Assert.Equal(56, MapFoliageInventory.ReferenceArray(4));
        Assert.Equal(408, MapFoliageInventory.Array(4, 96));
        // The old builder page remains live throughout the final column freeze.
        Assert.Equal(960, MapFoliageInventory.Construction(4, 3, 2, 1));
    }

    [Fact]
    public void Supported64BitLayouts_MatchActualObjectAndArrayAllocations()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Unsafe.SizeOf<MapFoliageAddress>());
        Assert.Equal(40, Unsafe.SizeOf<MapFoliageSampleKey>());
        Assert.Equal(24, Unsafe.SizeOf<MapPatchKey>());
        Assert.Equal(48, Unsafe.SizeOf<MapFaceKey>());
        Assert.Equal(56, Unsafe.SizeOf<MapFaceKey?>());
        string id = "invented";
        var policy = new MapFoliagePolicy(id, 1, 0);
        var materials = Array.AsReadOnly(new[] { id });
        object? held = Sample(id, policy, materials);
        var samples = new[] { (MapFoliageSample)held, (MapFoliageSample)held };
        var work = new MapFoliageWork(0, 0, 0, 0, 0, 0, 0, 0);
        _ = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, samples, work);
        _ = new object[3]; _ = new Vector3[3]; _ = new string('é', 1);
        long sample = AllocationMeasurement.Measure("foliage sample object", () => held = Sample(id, policy, materials));
        long array = AllocationMeasurement.Measure("foliage reference array", () => held = new object[3]);
        long positions = AllocationMeasurement.Measure("foliage position array", () => held = new Vector3[3]);
        long batch = AllocationMeasurement.Measure("foliage batch, wrapper and reference array", () =>
            held = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, samples, work));
        long policyBytes = AllocationMeasurement.Measure("foliage policy", () => held = new MapFoliagePolicy(id, 1, 0));
        long text = AllocationMeasurement.Measure("foliage UTF-16 text", () => held = new string('é', 1));
        GC.KeepAlive(held);
        output.WriteLine($"sample={sample}, references[3]={array}, positions[3]={positions}, batch+rows+wrapper={batch}, policy={policyBytes}, text={text}");
        Assert.Equal(248, sample);
        Assert.Equal(48, array);
        Assert.Equal(64, positions);
        Assert.Equal(128, batch);
        Assert.Equal(32, policyBytes);
        Assert.Equal(24, text);
    }

    static MapFoliageSample Sample(string id, MapFoliagePolicy policy, System.Collections.Generic.IReadOnlyList<string> materials) =>
        new(new(new(id, id, 1, 2), 1, policy), null, null, id, materials, 0, default, Quaternion.Identity, 1, Matrix4x4.Identity, 0, true);

    [Fact]
    public void EmptyFullBatches_CountTheSharedEmptyArrayOnce()
    {
        string id = new('é', 1);
        var work = new MapFoliageWork(0, 0, 0, 0, 0, 0, 0, 0);
        var first = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, [], work);
        var second = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, [], work);
        Assert.Same(first.Samples, second.Samples);
        // Two 64-byte batches, one shared 24-byte wrapper, one 24-byte empty array,
        // one 80-byte work object and one 24-byte string.
        Assert.Equal(280, MapFoliageInventory.FullSamples([first, second]));
    }

    [Fact]
    public void PublicEmptyMaterials_CountTheSharedEmptyWrapperAndArrayOnce()
    {
        string id = new('é', 1);
        var policy = new MapFoliagePolicy(id, 1, 0);
        var key = new MapFoliageSampleKey(new(id, id, 1, 2), 1, policy);
        var first = new MapFoliageSample(key, null, null, id, [], 0, default, Quaternion.Identity, 1, Matrix4x4.Identity, 0);
        var second = new MapFoliageSample(key, null, null, id, [], 0, default, Quaternion.Identity, 1, Matrix4x4.Identity, 0);
        var work = new MapFoliageWork(0, 2, 0, 0, 0, 0, 0, 0);
        var batch = new MapFoliageSampleBatch(id, default, MapFoliageStatus.Complete, id, [first, second], work);
        Assert.Same(first.MaterialResourceIds, second.MaterialResourceIds);
        // Non-material graph 760 plus one shared 24-byte wrapper and one 24-byte array.
        Assert.Equal(808, MapFoliageInventory.FullSamples([batch]));
    }
}
