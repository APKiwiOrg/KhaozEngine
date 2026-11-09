using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Full-cohort weld workspace ends between selected outputs. Every output keeps its own receipt.</summary>
internal static class MapRoofContributionPreparation
{
    internal static NativePropClusterCpuBuild Build(NativePropClusterRequest request)
    {
        var admission = MapSceneAdmission.Current;
        var sources = request.BorrowLayer.HlodSourceMeshes!;
        long mergeBytes = request.Instances.Sum(i => MapNativeInventory.Mesh(sources[i.AssetId]));
        return PropClusterRenderer.BuildNativeCpu(request, true, Select);

        GltfMesh Select(GltfMesh full, int start, int count, float weld)
        {
            using var preparation = admission?.BeginPreparation();
            admission?.ReserveBytes(checked(8 * mergeBytes));
            var mesh = PropHlod.SelectMergedContribution(full, start, count, weld);
            preparation?.Complete(MapNativeInventory.Mesh(mesh));
            return mesh;
        }
    }
}
