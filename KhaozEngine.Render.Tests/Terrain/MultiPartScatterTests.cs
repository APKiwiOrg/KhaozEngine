using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the multi-part scatter path (no GPU): a kit id maps to one-or-many
    /// <see cref="MeshHandle"/>s and <see cref="PropRenderer.Queue(SceneInstances, System.Collections.Generic.IReadOnlyList{PropPlacement}, System.Collections.Generic.IReadOnlyDictionary{string, System.Collections.Generic.IReadOnlyList{MeshHandle}}, Vector3, float, Color?, float, System.Collections.Generic.IReadOnlyDictionary{string, System.Collections.Generic.IReadOnlyList{MeshHandle}}, float, float)"/>
    /// queues one instance per (in-range placement, part) at the placement's shared world transform. A single-part
    /// list produces submissions byte-identical to the legacy single-handle path.</summary>
    public class MultiPartScatterTests
    {
        static IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> Parts(string id, params int[] slots)
        {
            var list = new MeshHandle[slots.Length];
            for (int i = 0; i < slots.Length; i++) list[i] = new MeshHandle(slots[i]);
            return new Dictionary<string, IReadOnlyList<MeshHandle>> { [id] = list };
        }

        [Fact]
        public void MultiPartProps_ScatterPath_DrawsEachPartInstanced()
        {
            // Two in-range placements, one culled. A 2-part kit => each in-range placement queues BOTH parts at that
            // placement's world transform (bark + leaves instance as a unit).
            var placements = new List<PropPlacement>
            {
                new PropPlacement("tree", 5f, 0f, 0f, 1f, 0f, 0),     // XZ dist 5
                new PropPlacement("tree", 5f, 0f, 3f, 1f, 0f, 0),     // XZ dist ~5.8
                new PropPlacement("tree", 500f, 0f, 0f, 1f, 0f, 0),   // culled
            };
            var parts = Parts("tree", 7, 8);
            var si = new SceneInstances();

            int drawn = PropRenderer.Queue(si, placements, parts, focus: Vector3.Zero, drawRadius: 50f);

            Assert.Equal(2, drawn);              // placements drawn (props), not part submissions
            Assert.Equal(4, si.Items.Count);     // 2 placements * 2 parts

            // Placement 1: both parts at (5,0,0).
            Assert.Equal(7, si.Items[0].Mesh.Index);
            Assert.Equal(8, si.Items[1].Mesh.Index);
            Assert.Equal(5f, si.Items[0].World.M41, 4);
            Assert.Equal(5f, si.Items[1].World.M41, 4);   // same transform for both parts
            Assert.Equal(0f, si.Items[0].World.M43, 4);

            // Placement 2: both parts at (5,0,3).
            Assert.Equal(7, si.Items[2].Mesh.Index);
            Assert.Equal(8, si.Items[3].Mesh.Index);
            Assert.Equal(3f, si.Items[2].World.M43, 4);
            Assert.Equal(3f, si.Items[3].World.M43, 4);
        }

        [Fact]
        public void MultiPartProps_UnknownId_Skipped()
        {
            var placements = new List<PropPlacement> { new PropPlacement("ghost", 1f, 0f, 1f, 1f, 0f, 0) };
            var si = new SceneInstances();

            int drawn = PropRenderer.Queue(si, placements, Parts("tree", 1, 2), focus: Vector3.Zero, drawRadius: 100f);

            Assert.Equal(0, drawn);
            Assert.Empty(si.Items);
        }

        [Fact]
        public void SinglePart_FastPath_ByteIdenticalSubmissions()
        {
            // The same scene queued via the legacy single-handle dict and via a 1-element multi-part list must yield
            // identical submissions (mesh slot, world matrix, tint) in the same order: the single-part path is a
            // performance-identical fast path, not a wrapper that reorders or re-tints.
            var placements = new List<PropPlacement>
            {
                new PropPlacement("rock", 3f, 4f, 5f, 2f, 0.5f, 0),
                new PropPlacement("rock", 40f, 0f, 0f, 1f, 0f, 0),   // culled by radius
                new PropPlacement("rock", -6f, 1f, 2f, 1.5f, 1.2f, 0),
            };
            var green = new Color(0.2f, 0.6f, 0.2f, 1f);

            var single = new Dictionary<string, MeshHandle> { ["rock"] = new MeshHandle(3) };
            var multi = Parts("rock", 3);

            var siOld = new SceneInstances();
            var siNew = new SceneInstances();
            int drawnOld = PropRenderer.Queue(siOld, placements, single, focus: Vector3.Zero, drawRadius: 20f, tint: green);
            int drawnNew = PropRenderer.Queue(siNew, placements, multi, focus: Vector3.Zero, drawRadius: 20f, tint: green);

            Assert.Equal(drawnOld, drawnNew);
            Assert.Equal(siOld.Items.Count, siNew.Items.Count);
            Assert.NotEmpty(siNew.Items);
            for (int i = 0; i < siOld.Items.Count; i++)
            {
                Assert.Equal(siOld.Items[i].Mesh.Index, siNew.Items[i].Mesh.Index);
                Assert.Equal(siOld.Items[i].World, siNew.Items[i].World);
                Assert.Equal(siOld.Items[i].Tint, siNew.Items[i].Tint);
            }
        }
    }
}
