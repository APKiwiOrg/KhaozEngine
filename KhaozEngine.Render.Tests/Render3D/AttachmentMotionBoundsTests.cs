using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

public sealed class AttachmentMotionBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chained_shared_meshes_grow_their_bounds_and_views_only_for_engine_leases(bool customLeases)
    {
        using var rig = new OutfitRig(customLeases: customLeases);
        SkinnedAttachmentGlbFixture glb = SkinnedAttachmentGlbFixture.Chained();
        // Make two separate chains rooted at hip, with rest positions (.25, 1, 0) and (4, 1, 0).
        glb.Nodes[0]!["children"] = new JsonArray(1, 2, 3);
        glb.Nodes[2]!.AsObject().Remove("children");
        glb.Nodes[3]!["translation"] = new JsonArray(4, 0, 0);
        glb.SetInverseBind(0, Matrix4x4.CreateTranslation(-4, -1, 0));
        glb.SetIndexedMaterialParts([0, 1, 2], 2);
        for (int vertex = 0; vertex < 3; vertex++)
            glb.SetElement(2, vertex, vertex == 1 ? 1 : 0, vertex == 1 ? 0 : 1, 0, 0);
        // Only (1, 0, 0) weights left1. The other vertices are weighted to knee only.
        AttachmentMeshRef mesh = rig.Source.Mesh("chain.glb", glb.Build());
        AttachmentMeshRef plainMesh = rig.Garment("plain.glb");
        var part = new SkinnedAttachmentPartDefinition(ChainPartId, mesh)
        {
            Regions = [new NamedRegionRun("alpha", 2)],
        };
        LoadedAttachmentSet original = rig.Load("original.json", new AttachmentSetDefinition([part,
            new SkinnedAttachmentPartDefinition(3, plainMesh) { Regions = [new NamedRegionRun("alpha", 4)] }], AttachmentLayer.Garment, []));
        var sources = original.Resources[0].SkinnedHandles;
        Assert.Equal(2, sources.Count);
        SkinnedMeshHandle plain = Assert.Single(original.Resources[1].SkinnedHandles);
        MeshBounds rest = rig.Scene.SkinnedBoundsAt(sources[0]);
        MeshBounds unchained = rig.Scene.SkinnedBoundsAt(plain);
        SkinnedMeshHandle live = rig.Scene.CreateSkinnedMeshView(sources[0], [0, 1, 2]);

        try
        {
            ChainDefinition left = new(["left0"], Vector3.UnitX * 2, FallSettings);
            ChainDefinition right = new(["left1"], Vector3.UnitZ * 3, FallSettings);
            Load("first.json", left, right);
            MeshBounds widest = rig.Scene.SkinnedBoundsAt(sources[0]);
            Load("second.json", left with { Tip = Vector3.UnitX * 7 }, right with { Tip = Vector3.UnitZ * .25f });
            SkinnedMeshHandle later = rig.Scene.CreateSkinnedMeshView(sources[0], [0, 1, 2]);
            try
            {
                Assert.Equal(unchained, rig.Scene.SkinnedBoundsAt(plain));
                foreach (SkinnedMeshHandle handle in new[] { sources[0], sources[1], live, later })
                {
                    MeshBounds bounds = rig.Scene.SkinnedBoundsAt(handle);
                    if (customLeases)
                    {
                        Assert.Equal(rest, bounds);
                        continue;
                    }
                    AssertContainsSphere(bounds, new Vector3(.25f, 1, 0), 7f);
                    AssertContainsSphere(bounds, new Vector3(4, 1, 0), 3f + MathF.Sqrt(10f));
                    Assert.Equal(widest.Max.X, bounds.Max.X);
                    Assert.Equal(4 + 3 + MathF.Sqrt(10f), bounds.Max.X, 5);
                }
            }
            finally { rig.Scene.UnloadSkinnedMesh(later); }
        }
        finally { rig.Scene.UnloadSkinnedMesh(live); }

        void Load(string key, ChainDefinition left, ChainDefinition right)
        {
            var motion = new ChainMotionDefinition([left, right], []);
            LoadedAttachmentSet loaded = rig.Load(key, new AttachmentSetDefinition([part with { Motion = motion }], AttachmentLayer.Garment, []));
            Assert.Equal(sources, loaded.Resources[0].SkinnedHandles);
        }
    }

    [Fact]
    public void First_upload_includes_the_chain_segments_and_tip()
    {
        using var rig = new OutfitRig();
        SkinnedAttachmentGlbFixture glb = SkinnedAttachmentGlbFixture.Chained();
        for (int vertex = 0; vertex < 3; vertex++)
            glb.SetElement(2, vertex, 0, 0, 0, 1); // hip only, so no vertex adds to reach
        LoadedAttachmentSet loaded = rig.Load("chain.json", ChainSet(rig.Source.Mesh("chain.glb", glb.Build())));
        SkinnedMeshHandle mesh = Assert.Single(loaded.Resources[0].SkinnedHandles);

        AssertContainsSphere(rig.Scene.SkinnedBoundsAt(mesh), new Vector3(.25f, 1, 0), 1f);
    }

    static void AssertContainsSphere(MeshBounds bounds, Vector3 center, float radius)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.True(bounds.Min[axis] <= center[axis] - radius);
            Assert.True(bounds.Max[axis] >= center[axis] + radius);
        }
        Assert.True(Vector3.Distance(bounds.Center, center) + radius <= bounds.Radius);
    }
}
