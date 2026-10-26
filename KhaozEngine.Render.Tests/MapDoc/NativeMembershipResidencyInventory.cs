using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Interiors;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Independent root model for footprint fixtures with empty terrain, placements, lights and foliage.</summary>
internal static class NativeMembershipResidencyInventory
{
    internal static long Preparation(IReadOnlyList<MapRenderPlan> plans, int cells)
    {
        long bytes = MembershipAdmissionFixtures.Footprint(cells) + MapMembershipCompactInventory.Cache;
        foreach (var plan in plans)
        {
            Assert.Empty(plan.Inventory.Placements.Inventory);
            Assert.Empty(plan.Inventory.Foliage.GenerationKeys);
            var kernel = (MapFoliageInputKernel)typeof(MapWindowFoliageContext)
                .GetField("_inputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plan.FoliageContext)!;
            long context = new MapAcquisitionInventoryOracle().Product(kernel.Resources) + 512 + 256 + 64;
            // Plan control, four empty batches, empty picker, private membership and this window's Group control.
            bytes += new MapAcquisitionInventoryOracle().Scene(plan.Scene) + context +
                1024 + 512 + 1024 + 512 + 512 + 512 + MembershipAdmissionFixtures.Private(cells: cells) + 512;
            bytes += 8L * plan.Inventory.Water.Cells.Count + 128L * plan.Resources.Count;
            foreach (var resource in plan.Resources)
            {
                var water = Assert.IsType<MapWaterResource>(resource.Payload);
                bytes += 512L + ModelVertex.SizeInBytes * (long)water.BorrowMesh.Vertices.Length +
                    4L * water.BorrowMesh.Indices32.Length + water.BorrowClipData.Length;
            }
        }
        return bytes;
    }
}
