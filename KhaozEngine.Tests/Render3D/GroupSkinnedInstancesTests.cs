using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of SkinnedModelRenderer.GroupSkinnedInstances: skinned draws are bucketed into
    /// contiguous per-mesh runs (first-seen order, prefix-sum offsets), and each instance keeps its own bone
    /// offset so instances of one mesh draw in a single instanced call yet read distinct bone ranges.</summary>
    public class GroupSkinnedInstancesTests
    {
        static SkinnedSceneInstances.Instance Inst(int mesh, uint boneOffset, Color tint) => new(
            new SkinnedMeshHandle(mesh), Matrix4x4.Identity, tint, Material.None, boneOffset);

        [Fact]
        public void InterleavedMeshes_BucketedContiguously_BoneOffsetsPreserved()
        {
            var items = new List<SkinnedSceneInstances.Instance>
            {
                Inst(0, 0,  Color.White),   // mesh 0, bones [0..)
                Inst(1, 8,  Color.White),   // mesh 1
                Inst(0, 16, Color.White),   // mesh 0 again, different bone range
            };
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();
            var runs = new List<Scene3D.SkinnedMeshRun>();

            SkinnedModelRenderer.GroupSkinnedInstances(items, data, runs);

            Assert.Equal(2, runs.Count);
            Assert.Equal(0, runs[0].Mesh.Index);
            Assert.Equal(0u, runs[0].Start);
            Assert.Equal(2u, runs[0].Count);          // two instances of mesh 0, contiguous
            Assert.Equal(1, runs[1].Mesh.Index);
            Assert.Equal(2u, runs[1].Start);
            // mesh-0 instances keep their own bone offsets even after reordering.
            Assert.Equal(0f, data[0].BoneOffset);
            Assert.Equal(16f, data[1].BoneOffset);
            Assert.Equal(8f, data[2].BoneOffset);     // mesh 1
        }

        [Fact]
        public void Empty_ProducesNoRuns()
        {
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();
            var runs = new List<Scene3D.SkinnedMeshRun>();
            SkinnedModelRenderer.GroupSkinnedInstances(new List<SkinnedSceneInstances.Instance>(), data, runs);
            Assert.Empty(runs);
            Assert.Empty(data);
        }

        [Fact]
        public void SkinnedInstanceData_StrideMatchesManagedSize()
        {
            Assert.Equal(116u, SkinnedModelRenderer.SkinnedInstanceData.SizeInBytes);
            Assert.Equal(116, Marshal.SizeOf<SkinnedModelRenderer.SkinnedInstanceData>());
        }
    }
}
