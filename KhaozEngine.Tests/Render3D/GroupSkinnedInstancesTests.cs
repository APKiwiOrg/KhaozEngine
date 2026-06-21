using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of SkinnedModelRenderer.BuildInstanceData: each queued skinned draw becomes one
    /// instance entry in submission order (no grouping, since each skinned draw renders separately with its own
    /// dynamic-offset bone slot), mapping World/Tint/Material onto the per-instance stream.</summary>
    public class GroupSkinnedInstancesTests
    {
        static SkinnedSceneInstances.Instance Inst(int mesh, float tx, Color tint) =>
            new(new SkinnedMeshHandle(mesh), Matrix4x4.CreateTranslation(tx, 0, 0), tint, Material.None);

        [Fact]
        public void BuildInstanceData_PreservesSubmissionOrder_AndMapsFields()
        {
            var items = new List<SkinnedSceneInstances.Instance>
            {
                Inst(0, 1f, Color.White),
                Inst(1, 2f, new Color(1f, 0f, 0f, 1f)),
                Inst(0, 3f, Color.White),
            };
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();

            SkinnedModelRenderer.BuildInstanceData(items, data);

            Assert.Equal(3, data.Count);
            Assert.Equal(1f, data[0].Model.M41, 4);
            Assert.Equal(2f, data[1].Model.M41, 4);
            Assert.Equal(3f, data[2].Model.M41, 4);
            Assert.Equal(new Vector4(1, 0, 0, 1), data[1].Tint);
        }

        [Fact]
        public void BuildInstanceData_Empty_ProducesNoInstances()
        {
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();
            SkinnedModelRenderer.BuildInstanceData(new List<SkinnedSceneInstances.Instance>(), data);
            Assert.Empty(data);
        }

        [Fact]
        public void SkinnedInstanceData_StrideIs16Aligned()
        {
            // The per-instance vertex stride must be a multiple of 16 (a non-16 stride mis-fetches the last
            // attribute for instances past the first on Metal/Veldrid).
            Assert.Equal(112u, SkinnedModelRenderer.SkinnedInstanceData.SizeInBytes);
            Assert.Equal(112, Marshal.SizeOf<SkinnedModelRenderer.SkinnedInstanceData>());
            Assert.Equal(0u, SkinnedModelRenderer.SkinnedInstanceData.SizeInBytes % 16);
        }

        [Fact]
        public void BoneSlot_IsA256ByteAlignedWindow()
        {
            // Each per-draw dynamic offset is slot * SlotBytes; SlotBytes must be 256-aligned so any slot offset
            // satisfies the backend uniform-buffer offset alignment.
            Assert.Equal(0u, SkinnedModelRenderer.SlotBytes % 256u);
            Assert.Equal((uint)SkinnedModelRenderer.MaxBonesPerDraw * 64u, SkinnedModelRenderer.SlotBytes);
        }
    }
}
