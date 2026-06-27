using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the instanced prop render helper (no GPU): it queues SceneInstances.Add for
    /// placements within the horizontal draw radius of the focus point, distance-culls the rest, skips unknown
    /// ids, and builds the scale/yaw/translation world matrix.</summary>
    public class PropRendererTests
    {
        static Dictionary<string, MeshHandle> Meshes(params (string id, int slot)[] entries)
        {
            var d = new Dictionary<string, MeshHandle>();
            foreach (var (id, slot) in entries) d[id] = new MeshHandle(slot);
            return d;
        }

        [Fact]
        public void Queue_InRangeQueued_OutOfRangeCulled()
        {
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine_a", 5f, 0f, 0f, 1f, 0f, 0),     // XZ dist 5 from origin
                new PropPlacement("pine_a", 500f, 0f, 0f, 1f, 0f, 0),   // XZ dist 500 from origin
            };
            var meshes = Meshes(("pine_a", 7));
            var si = new SceneInstances();

            int queued = PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: 50f);

            Assert.Equal(1, queued);
            Assert.Single(si.Items);
            Assert.Equal(7, si.Items[0].Mesh.Index);
            Assert.Equal(5f, si.Items[0].World.M41, 4);     // the near placement's X
        }

        [Fact]
        public void Queue_HorizontalCull_IgnoresHeight()
        {
            // A placement directly above the focus but far in Y is still in range (cull is XZ-only).
            var placements = new List<PropPlacement> { new PropPlacement("rock_a", 1f, 400f, 1f, 1f, 0f, 0) };
            var meshes = Meshes(("rock_a", 2));
            var si = new SceneInstances();

            int queued = PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: 10f);

            Assert.Equal(1, queued);
        }

        [Fact]
        public void Queue_UnknownId_Skipped()
        {
            var placements = new List<PropPlacement> { new PropPlacement("ghost", 1f, 0f, 1f, 1f, 0f, 0) };
            var meshes = Meshes(("pine_a", 1));
            var si = new SceneInstances();

            int queued = PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: 100f);

            Assert.Equal(0, queued);
            Assert.Empty(si.Items);
        }

        [Fact]
        public void Queue_BuildsScaleYawTranslationMatrix()
        {
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", 3f, 4f, 5f, 2f, 0f, 0) };
            var meshes = Meshes(("pine_a", 0));
            var si = new SceneInstances();

            PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: 100f);

            Matrix4x4 w = si.Items[0].World;
            Assert.Equal(3f, w.M41, 4);     // translation X
            Assert.Equal(4f, w.M42, 4);     // translation Y
            Assert.Equal(5f, w.M43, 4);     // translation Z
            Assert.Equal(2f, w.M11, 4);     // uniform scale * cos(0)
        }

        [Fact]
        public void Queue_AppliesTint()
        {
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", 0f, 0f, 0f, 1f, 0f, 0) };
            var meshes = Meshes(("pine_a", 0));
            var si = new SceneInstances();
            var green = new Color(0.2f, 0.6f, 0.2f, 1f);

            PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: 10f, tint: green);

            Assert.Equal(green, si.Items[0].Tint);
        }
    }
}
