using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Render3D
{
    /// <summary>One source material's directly indexed skinned geometry and decoded texture maps.
    /// Parts imported together share their skeleton and skin-order bind metadata.</summary>
    public sealed record GltfSkinnedMeshPart(SkinnedGltfMesh Mesh, GltfMaterialMaps Maps)
    {
        /// <summary>Cold host-binding admission. Annotated hosts require every material slice, unchanged source
        /// evidence, one exact source/map owner and one skeleton owner with identical skin-order matrices.
        /// Ordinary individual uploads remain independent of complete-host admission.</summary>
        internal static void ValidateCoverageParts(HostCoveragePart coverage, IReadOnlyList<SkinnedGltfMesh> meshes)
        {
            ArgumentNullException.ThrowIfNull(coverage);
            ArgumentNullException.ThrowIfNull(meshes);
            SkinnedGltfMesh? first = null;
            var expected = coverage.Source.Vertices.Select(v => v.MaterialSlice).Distinct().Order().ToArray();
            var found = new HashSet<int>();
            foreach (var mesh in meshes)
            {
                ArgumentNullException.ThrowIfNull(mesh);
                first ??= mesh;
                if (mesh.Coverage is not { } part)
                    throw coverage.Source.Error(HostCoverageRefusal.UnannotatedMaterialSlice, "An annotated host contains an unannotated slice.");
                if (!ReferenceEquals(part.Source, coverage.Source) || !ReferenceEquals(part.Map, coverage.Map))
                    throw coverage.Source.Error(HostCoverageRefusal.SourceOwnershipMismatch, "Material slices do not share source evidence.");
                if (!found.Add(part.MaterialSlice))
                    throw new HostCoverageDataException(HostCoverageRefusal.DuplicateMaterialSlice, "An annotated material slice is repeated.")
                    { Identity = coverage.Source.Identity, MaterialSlice = part.MaterialSlice };
                if (!ReferenceEquals(mesh.CoverageSkeletonOwner ?? mesh.Skeleton, part.SkeletonOwner)
                    || !ReferenceEquals(part.SkeletonOwner, coverage.SkeletonOwner))
                    throw coverage.Source.Error(HostCoverageRefusal.SkeletonMismatch, "Material slice has another skeleton owner.", materialSlice: part.MaterialSlice);
                if (!part.Matches(mesh)) throw coverage.Source.Error(HostCoverageRefusal.MeshChanged, "Annotated mesh changed after source binding.", materialSlice: part.MaterialSlice);
                if (!mesh.InverseBind.SequenceEqual(first.InverseBind)
                    || !mesh.RestPose.SequenceEqual(first.RestPose))
                    throw coverage.Source.Error(HostCoverageRefusal.SkeletonMismatch, "Material slices do not share the same skin.", materialSlice: part.MaterialSlice);
            }
            int[] missing = expected.Where(slice => !found.Contains(slice)).ToArray();
            if (missing.Length > 0)
                throw new HostCoverageDataException(HostCoverageRefusal.MissingMaterialSlices, "The annotated host is missing material slices.")
                { Identity = coverage.Source.Identity, MissingMaterialSlices = missing, Count = found.Count, Limit = expected.Length };
        }
    }
}
