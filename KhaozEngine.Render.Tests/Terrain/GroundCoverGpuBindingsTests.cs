using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

[Collection("AllocSensitive")]
public sealed class GroundCoverGpuBindingsTests
{
    static GroundCoverInstance Instance(string id, float x = 1f, float rank = .25f) => new(
        id, new Vector3(x, 2f, 3f),
        Matrix4x4.CreateRotationZ(.3f) * Matrix4x4.CreateTranslation(x, 2f, 3f), rank);

    [Fact]
    public void MultipartExpansionPreservesAuthoredTransformsAndRanks()
    {
        var cover = new GroundCoverBatch([Instance("grass"), Instance("missing", 2f), Instance("clover", 3f, .75f)]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["grass"] = [new MeshHandle(4, 2), new MeshHandle(7, 3)],
            ["clover"] = [new MeshHandle(9, 1)],
        };
        var bindings = new GroundCoverGpuBindings(cover);
        bindings.Refresh(meshes);

        FoliageInstance[] expanded = bindings.Expand();

        Assert.Equal(3, expanded.Length);
        Assert.Equal(new FoliageInstance(new MeshHandle(4, 2), cover[0].Transform, .25f), expanded[0]);
        Assert.Equal(new FoliageInstance(new MeshHandle(7, 3), cover[0].Transform, .25f), expanded[1]);
        Assert.Equal(new FoliageInstance(new MeshHandle(9, 1), cover[2].Transform, .75f), expanded[2]);
    }

    [Fact]
    public void InPlacePartEditsInvalidateTheSnapshotIncludingHandleGeneration()
    {
        var cover = new GroundCoverBatch([Instance("grass")]);
        var parts = new List<MeshHandle> { new(4, 1) };
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["grass"] = parts };
        var bindings = new GroundCoverGpuBindings(cover);
        Assert.True(bindings.Refresh(meshes));
        Assert.False(bindings.Refresh(meshes));

        parts[0] = new MeshHandle(4, 2);

        Assert.True(bindings.Refresh(meshes));
        Assert.Equal(2, Assert.Single(bindings.Expand()).Mesh.Generation);
        Assert.False(bindings.Refresh(meshes));

        parts.Add(new MeshHandle(7, 1));
        Assert.True(bindings.Refresh(meshes));
        Assert.Equal(2, bindings.Expand().Length);
        parts.RemoveAt(0);
        Assert.True(bindings.Refresh(meshes));
        Assert.Equal(7, Assert.Single(bindings.Expand()).Mesh.Index);
    }

    [Fact]
    public void MissingRemovedAndRestoredBindingsRebuildTheirInstances()
    {
        var cover = new GroundCoverBatch([Instance("grass")]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>();
        var bindings = new GroundCoverGpuBindings(cover);
        bindings.Refresh(meshes);
        Assert.Empty(bindings.Expand());

        meshes["grass"] = [new MeshHandle(4, 1)];
        Assert.True(bindings.Refresh(meshes));
        Assert.Equal(4, Assert.Single(bindings.Expand()).Mesh.Index);
        meshes.Remove("grass");
        Assert.True(bindings.Refresh(meshes));
        Assert.Empty(bindings.Expand());
        meshes["grass"] = [new MeshHandle(4, 2)];
        Assert.True(bindings.Refresh(meshes));
        Assert.Equal(2, Assert.Single(bindings.Expand()).Mesh.Generation);
    }

    [Fact]
    public void EqualReplacementListsAndUnrelatedModelsDoNotCauseRebuilds()
    {
        var cover = new GroundCoverBatch([Instance("grass")]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["grass"] = [new MeshHandle(4, 1)],
        };
        var bindings = new GroundCoverGpuBindings(cover);
        bindings.Refresh(meshes);
        meshes["grass"] = new List<MeshHandle> { new(4, 1) };
        meshes["unrelated"] = [new MeshHandle(9, 1)];

        Assert.False(bindings.Refresh(meshes));
        Assert.Equal(4, Assert.Single(bindings.Expand()).Mesh.Index);
    }

    [Fact]
    public void RepeatedRefreshesReadOnlyUniqueModelsAndAllocateNothing()
    {
        var comparer = new CountingComparer();
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>(comparer)
        {
            ["grass"] = [new MeshHandle(4, 1)],
            ["clover"] = [new MeshHandle(9, 1)],
        };
        var cover = new GroundCoverBatch(Enumerable.Range(0, 4096)
            .Select(i => Instance(i % 2 == 0 ? "grass" : "clover", i * .001f)).ToArray());
        var bindings = new GroundCoverGpuBindings(cover);
        Assert.True(bindings.Refresh(meshes));
        for (int i = 0; i < 20; i++) bindings.Refresh(meshes);
        comparer.HashCalls = 0;

        Assert.False(bindings.Refresh(meshes));

        Assert.Equal(2, comparer.HashCalls);
        AllocAssert.NoPerCallAllocation("checking retained ground-cover model bindings", () =>
        {
            for (int i = 0; i < 20; i++) bindings.Refresh(meshes);
        });
    }

    sealed class CountingComparer : IEqualityComparer<string>
    {
        public int HashCalls;
        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);
        public int GetHashCode(string value)
        {
            HashCalls++;
            return StringComparer.Ordinal.GetHashCode(value);
        }
    }
}
