using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of <see cref="Scene3D.GroupInstances"/>: the pure grouping that GPU instancing relies on.
    /// Verifies instances are bucketed into contiguous per-mesh runs, runs are first-seen ordered with correct
    /// prefix-sum offsets, the flat instance array maps World/Tint/Material onto InstanceData, and the reused
    /// output buffers are cleared (not appended) across calls.
    /// </summary>
    public class Scene3DGroupInstancesTests
    {
        static SceneInstances.Instance Inst(int mesh, float tx, Color tint)
            => new(new MeshHandle(mesh), Matrix4x4.CreateTranslation(tx, 0, 0), tint);

        [Fact]
        public void SingleMesh_OneRun_FlatArrayMatchesInput()
        {
            var items = new List<SceneInstances.Instance>
            {
                Inst(0, 1f, Color.White),
                Inst(0, 2f, new Color(1f, 0f, 0f, 1f)),
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();

            Scene3D.GroupInstances(items, data, runs);

            Assert.Single(runs);
            Assert.Equal(0, runs[0].Mesh.Index);
            Assert.Equal(0u, runs[0].Start);
            Assert.Equal(2u, runs[0].Count);
            Assert.Equal(2, data.Count);
            Assert.Equal(1f, data[0].Model.M41, 4);
            Assert.Equal(2f, data[1].Model.M41, 4);
            Assert.Equal(new Vector4(1, 0, 0, 1), data[1].Tint);
        }

        [Fact]
        public void InterleavedMeshes_BucketedContiguously_FirstSeenOrder()
        {
            // Submission order: mesh 5, 2, 5, 2, 5 -> two runs in first-seen order (5 then 2), each contiguous.
            var items = new List<SceneInstances.Instance>
            {
                Inst(5, 10f, Color.White),
                Inst(2, 20f, Color.White),
                Inst(5, 11f, Color.White),
                Inst(2, 21f, Color.White),
                Inst(5, 12f, Color.White),
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();

            Scene3D.GroupInstances(items, data, runs);

            Assert.Equal(2, runs.Count);
            Assert.Equal(5, runs[0].Mesh.Index);
            Assert.Equal(0u, runs[0].Start);
            Assert.Equal(3u, runs[0].Count);
            Assert.Equal(2, runs[1].Mesh.Index);
            Assert.Equal(3u, runs[1].Start);
            Assert.Equal(2u, runs[1].Count);

            Assert.Equal(5, data.Count);
            // Mesh-5 instances (in submission order within the bucket): tx 10, 11, 12.
            Assert.Equal(10f, data[0].Model.M41, 4);
            Assert.Equal(11f, data[1].Model.M41, 4);
            Assert.Equal(12f, data[2].Model.M41, 4);
            // Mesh-2 instances: tx 20, 21.
            Assert.Equal(20f, data[3].Model.M41, 4);
            Assert.Equal(21f, data[4].Model.M41, 4);
        }

        [Fact]
        public void MapsMaterial_Onto_EmissiveAndSpecParams()
        {
            var glow = new Color(0.8f, 0.2f, 0.1f, 1f);
            var items = new List<SceneInstances.Instance>
            {
                new(new MeshHandle(0), Matrix4x4.Identity, Color.White, new Material(glow, 0.7f, 64f)),
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();

            Scene3D.GroupInstances(items, data, runs);

            Assert.Equal((Vector4)glow, data[0].Emissive);
            Assert.Equal(0.7f, data[0].SpecParams.X, 4);
            Assert.Equal(64f, data[0].SpecParams.Y, 4);
        }

        [Fact]
        public void ApplyAlphaCutoffs_WritesMaskCutoffIntoSpecParamsZ_PerRun_LeavesOpaqueUntouched()
        {
            // Two runs: mesh 5 is MASK (cutoff 0.3), mesh 2 is OPAQUE (cutoff 0). Interleaved -> contiguous runs.
            var items = new List<SceneInstances.Instance>
            {
                new(new MeshHandle(5), Matrix4x4.Identity, Color.White, new Material(Color.Transparent, 0.5f, 40f)),
                new(new MeshHandle(2), Matrix4x4.Identity, Color.White, new Material(Color.Transparent, 0.5f, 40f)),
                new(new MeshHandle(5), Matrix4x4.Identity, Color.White, new Material(Color.Transparent, 0.5f, 40f)),
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();
            Scene3D.GroupInstances(items, data, runs);

            Scene3D.ApplyAlphaCutoffs(data, runs, h => h.Index == 5 ? 0.3f : 0f);

            // Mesh-5 (MASK) instances carry the cutoff in .z; strength/shininess (.x/.y) are preserved.
            Assert.Equal(0.3f, data[0].SpecParams.Z, 4);
            Assert.Equal(0.3f, data[1].SpecParams.Z, 4);
            Assert.Equal(0.5f, data[0].SpecParams.X, 4);
            Assert.Equal(40f, data[0].SpecParams.Y, 4);
            // Mesh-2 (OPAQUE) instance is untouched: .z stays 0 (byte-identical to the pre-cutout packing).
            Assert.Equal(0f, data[2].SpecParams.Z, 4);
        }

        [Fact]
        public void ApplyAlphaCutoffs_AllOpaque_IsNoOp()
        {
            var items = new List<SceneInstances.Instance>
            {
                Inst(0, 1f, Color.White),
                Inst(0, 2f, Color.White),
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();
            Scene3D.GroupInstances(items, data, runs);

            Scene3D.ApplyAlphaCutoffs(data, runs, _ => 0f);   // every mesh OPAQUE

            Assert.Equal(0f, data[0].SpecParams.Z, 4);
            Assert.Equal(0f, data[1].SpecParams.Z, 4);
        }

        [Fact]
        public void Empty_ClearsBuffers()
        {
            var data = new List<ModelRenderer.InstanceData> { default, default };
            var runs = new List<Scene3D.MeshRun> { new(9, 0, 1) };

            Scene3D.GroupInstances(new List<SceneInstances.Instance>(), data, runs);

            Assert.Empty(data);
            Assert.Empty(runs);
        }

        [Fact]
        public void Reused_Buffers_AreClearedNotAppended()
        {
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();

            var first = new List<SceneInstances.Instance> { Inst(1, 0f, Color.White), Inst(1, 0f, Color.White) };
            Scene3D.GroupInstances(first, data, runs);
            Assert.Equal(2, data.Count);

            var second = new List<SceneInstances.Instance> { Inst(7, 0f, Color.White) };
            Scene3D.GroupInstances(second, data, runs);

            Assert.Single(data);
            Assert.Single(runs);
            Assert.Equal(7, runs[0].Mesh.Index);
        }
    }
}
