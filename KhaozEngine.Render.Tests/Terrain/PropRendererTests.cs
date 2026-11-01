using System;
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

        [Theory]
        [InlineData("rock_a", 400f, "rock_a", 2, 10f, 1)]
        [InlineData("ghost", 0f, "pine_a", 1, 100f, 0)]
        public void Queue_UsesHorizontalRangeAndKnownMeshIds(
            string placementId, float height, string meshId, int meshSlot, float radius, int expectedCount)
        {
            var placements = new List<PropPlacement> { new PropPlacement(placementId, 1f, height, 1f, 1f, 0f, 0) };
            var meshes = Meshes((meshId, meshSlot));
            var si = new SceneInstances();

            int queued = PropRenderer.Queue(si, placements, meshes, focus: Vector3.Zero, drawRadius: radius);

            Assert.Equal(expectedCount, queued);
            Assert.Equal(expectedCount, si.Items.Count);
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

        // ---- Fade band (issue #44): dissolve ramps 0..1 over [drawRadius - fadeBandWidth, drawRadius] ----

        static Dictionary<string, IReadOnlyList<MeshHandle>> Parts(params (string id, int[] slots)[] entries)
        {
            var d = new Dictionary<string, IReadOnlyList<MeshHandle>>();
            foreach (var (id, slots) in entries)
            {
                var list = new MeshHandle[slots.Length];
                for (int i = 0; i < slots.Length; i++) list[i] = new MeshHandle(slots[i]);
                d[id] = list;
            }
            return d;
        }

        // Place a single "pine_a" at horizontal distance `dist` along +X and read back its dissolve threshold.
        static float DissolveAtDistance(float dist, float drawRadius, float fadeBandWidth)
        {
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", dist, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Meshes(("pine_a", 3)), Vector3.Zero, drawRadius, fadeBandWidth: fadeBandWidth);
            return si.Items.Count == 1 ? si.Items[0].DissolveThreshold : float.NaN;
        }

        [Theory]
        [InlineData(0f, 50f, 99f)]  // hard cut
        [InlineData(40f, 50f, 60f)] // interior and exact inner edge
        public void FadeBand_KeepsPropsSolidOutsideTheFade(
            float fadeWidth, float firstDistance, float secondDistance)
        {
            Assert.Equal(0f, DissolveAtDistance(firstDistance, drawRadius: 100f, fadeWidth), 5);
            Assert.Equal(0f, DissolveAtDistance(secondDistance, drawRadius: 100f, fadeWidth), 5);
        }

        [Theory]
        [InlineData(40f, 80f, 0.5f)]
        [InlineData(40f, 90f, 0.75f)]
        [InlineData(40f, 100f, 1f)]
        [InlineData(200f, 0f, 0f)]
        [InlineData(200f, 50f, 0.5f)]
        [InlineData(200f, 100f, 1f)]
        public void FadeBand_RampsByDistanceAndClampsItsWidth(float fadeWidth, float distance, float expectedDissolve)
        {
            Assert.Equal(expectedDissolve, DissolveAtDistance(distance, drawRadius: 100f, fadeWidth), 4);
        }

        [Fact]
        public void FadeBand_Deterministic_SameDistanceSameDissolve()
        {
            // No per-frame randomness: two reads of the same placement/config yield the identical dissolve.
            float a = DissolveAtDistance(dist: 85f, drawRadius: 100f, fadeBandWidth: 40f);
            float b = DissolveAtDistance(dist: 85f, drawRadius: 100f, fadeBandWidth: 40f);
            Assert.Equal(a, b);
        }

        [Fact]
        public void FadeBand_OutOfRange_StillCulled()
        {
            // The fade band does not extend the draw radius: past drawRadius the prop is culled, not drawn dissolved.
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", 150f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            int n = PropRenderer.Queue(si, placements, Meshes(("pine_a", 3)), Vector3.Zero, drawRadius: 100f, fadeBandWidth: 40f);
            Assert.Equal(0, n);
            Assert.Empty(si.Items);
        }

        [Fact]
        public void FadeBand_Parts_AppliesOneDissolveToEveryPart()
        {
            // A multi-part prop in the band: all of its parts carry the same dissolve so the whole prop fades coherently.
            var placements = new List<PropPlacement> { new PropPlacement("tree", 80f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            int n = PropRenderer.Queue(si, placements, Parts(("tree", new[] { 1, 2, 3 })), Vector3.Zero,
                                       drawRadius: 100f, fadeBandWidth: 40f);
            Assert.Equal(1, n);
            Assert.Equal(3, si.Items.Count);
            foreach (var it in si.Items) Assert.Equal(0.5f, it.DissolveThreshold, 4);
        }

        // ---- LOD mesh variant selection: beyond lodDistance, a kit swaps to its far mesh (per-kit opt-in) ----

        static int MeshSlotAtDistance(float dist, float lodDistance,
            Dictionary<string, MeshHandle> meshes, Dictionary<string, MeshHandle>? lod)
        {
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", dist, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, meshes, Vector3.Zero, drawRadius: 400f,
                               lodMeshes: lod, lodDistance: lodDistance);
            return si.Items.Count == 1 ? si.Items[0].Mesh.Index : -1;
        }

        [Theory]
        [InlineData(50f, 100f, "pine_a", 7)]  // near
        [InlineData(150f, 100f, "pine_a", 50)] // far
        [InlineData(150f, 100f, "rock_a", 7)] // another kit has the variant
        [InlineData(300f, 0f, "pine_a", 7)]   // switching disabled
        [InlineData(300f, 100f, null, 7)]     // no variant table
        public void Lod_SelectsAVariantOnlyWhenAvailableAndBeyondAnEnabledDistance(
            float distance, float lodDistance, string? variantId, int expectedSlot)
        {
            var variants = variantId is null ? null : Meshes((variantId, 50));
            int slot = MeshSlotAtDistance(distance, lodDistance, Meshes(("pine_a", 7)), variants);
            Assert.Equal(expectedSlot, slot);
        }

        [Fact]
        public void Lod_Parts_BeyondDistance_SelectsVariantParts()
        {
            // Multi-part LOD: past lodDistance the whole prop switches to the variant's part list.
            var placements = new List<PropPlacement> { new PropPlacement("tree", 150f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            int n = PropRenderer.Queue(si, placements, Parts(("tree", new[] { 1, 2, 3 })), Vector3.Zero,
                                       drawRadius: 400f, lodParts: Parts(("tree", new[] { 90 })), lodDistance: 100f);
            Assert.Equal(1, n);
            Assert.Single(si.Items);                 // the LOD variant is a single simplified part
            Assert.Equal(90, si.Items[0].Mesh.Index);
        }

        [Fact]
        public void Lod_Parts_WithinDistance_UsesFullParts()
        {
            var placements = new List<PropPlacement> { new PropPlacement("tree", 50f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Parts(("tree", new[] { 1, 2, 3 })), Vector3.Zero,
                               drawRadius: 400f, lodParts: Parts(("tree", new[] { 90 })), lodDistance: 100f);
            Assert.Equal(3, si.Items.Count);         // near: all three full parts
        }

        static SceneInstances.Instance[] LodItemsAtDistance(float dist, float width)
        {
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", dist, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Meshes(("pine_a", 7)), Vector3.Zero, drawRadius: 400f,
                lodMeshes: Meshes(("pine_a", 50)), lodDistance: 64f, lodCrossfadeWidth: width);
            var result = new SceneInstances.Instance[si.Items.Count];
            for (int i = 0; i < result.Length; i++) result[i] = si.Items[i];
            return result;
        }

        [Theory]
        [InlineData(55.99f, 1, 7)]
        [InlineData(56f, 2, 7)]
        [InlineData(64f, 2, 7)]
        [InlineData(71.99f, 2, 7)]
        [InlineData(72f, 1, 50)]
        public void LodCrossfade_SubmitsOnlyTheRepresentationsOwnedByTheBand(float distance, int count, int firstSlot)
        {
            SceneInstances.Instance[] items = LodItemsAtDistance(distance, width: 16f);

            Assert.Equal(count, items.Length);
            Assert.Equal(firstSlot, items[0].Mesh.Index);
            if (count == 2) Assert.Equal(50, items[1].Mesh.Index);
        }

        [Fact]
        public void LodCrossfade_MidpointUsesEqualThresholdsAndComplementaryPhases()
        {
            SceneInstances.Instance[] items = LodItemsAtDistance(64f, width: 16f);

            Assert.Equal(0.5f, items[0].DissolveThreshold, 5);
            Assert.Equal(0.5f, items[1].DissolveThreshold, 5);
            Assert.Equal(0f, items[0].DissolveComplement);
            Assert.Equal(1f, items[1].DissolveComplement);
        }

        [Fact]
        public void LodCrossfade_ComposesTheOuterFadeWithoutRestoringCoverage()
        {
            var placements = new[] { new PropPlacement("pine_a", 80f, 0f, 0f, 1f, 0f, 0) };
            var instances = new SceneInstances();

            PropRenderer.Queue(instances, placements, Meshes(("pine_a", 7)), Vector3.Zero,
                drawRadius: 100f, lodCrossfadeWidth: 40f, fadeBandWidth: 40f,
                lodMeshes: Meshes(("pine_a", 50)), lodDistance: 80f);

            Assert.Equal(2, instances.Items.Count);
            Assert.Equal(0.75f, instances.Items[0].DissolveThreshold, 5);
            Assert.Equal(0f, instances.Items[0].DissolveComplement);
            Assert.Equal(0.25f, instances.Items[1].DissolveThreshold, 5);
            Assert.Equal(1f, instances.Items[1].DissolveComplement);
        }

        [Fact]
        public void MultipartLodCrossfade_ComposesTheHlodFloorWithoutRestoringCoverage()
        {
            var placements = new[] { new PropPlacement("tree", 70f, 0f, 0f, 1f, 0f, 0) };
            var instances = new SceneInstances();

            PropRenderer.Queue(instances, placements, Parts(("tree", new[] { 1, 2 })), Vector3.Zero,
                drawRadius: 200f, lodCrossfadeWidth: 40f,
                lodParts: Parts(("tree", new[] { 90 })), lodDistance: 80f, dissolveFloor: 0.6f);

            Assert.Equal(3, instances.Items.Count);
            for (int i = 0; i < 2; i++)
            {
                SceneInstances.Instance item = instances.Items[i];
                Assert.Equal(0.7f, item.DissolveThreshold, 5);
                Assert.Equal(0f, item.DissolveComplement);
            }
            Assert.Equal(0.1f, instances.Items[2].DissolveThreshold, 5);
            Assert.Equal(1f, instances.Items[2].DissolveComplement);
        }

        [Fact]
        public void LodCrossfade_BandEdgesKeepComplementaryOwnership()
        {
            SceneInstances.Instance[] start = LodItemsAtDistance(56f, width: 16f);
            Assert.Equal(0f, start[0].DissolveThreshold, 5);
            Assert.Equal(0f, start[1].DissolveThreshold, 5);
            Assert.Equal(0f, start[0].DissolveComplement);
            Assert.Equal(1f, start[1].DissolveComplement);

            SceneInstances.Instance[] end = LodItemsAtDistance(71.99f, width: 16f);
            Assert.InRange(end[0].DissolveThreshold, 0.9993f, 0.9994f);
            Assert.Equal(end[0].DissolveThreshold, end[1].DissolveThreshold);
            Assert.Equal(0f, end[0].DissolveComplement);
            Assert.Equal(1f, end[1].DissolveComplement);
        }

        [Fact]
        public void LodCrossfade_ZeroWidthPreservesTheHardSwap()
        {
            SceneInstances.Instance[] at = LodItemsAtDistance(64f, width: 0f);
            SceneInstances.Instance[] beyond = LodItemsAtDistance(64.01f, width: 0f);

            Assert.Single(at);
            Assert.Equal(7, at[0].Mesh.Index);
            Assert.Equal(0f, at[0].DissolveThreshold);
            Assert.Equal(0f, at[0].DissolveComplement);
            Assert.Single(beyond);
            Assert.Equal(50, beyond[0].Mesh.Index);
            Assert.Equal(0f, beyond[0].DissolveThreshold);
            Assert.Equal(0f, beyond[0].DissolveComplement);
        }

        [Fact]
        public void PropLayer_CarriesLodCrossfadeWidthThroughHlodCopy()
        {
            PropLayer layer = PropLayer.PlacementLayer(Array.Empty<PropPlacement>(), Meshes(("pine_a", 7)), 400f,
                lodMeshes: Meshes(("pine_a", 50)), lodDistance: 64f).WithLodCrossfade(16f);
            PropLayer hlod = layer.WithHlod(new Dictionary<string, GltfMesh>(), 192f, 1.5f, 32f);

            Assert.Equal(16f, layer.LodCrossfadeWidth);
            Assert.Equal(16f, hlod.LodCrossfadeWidth);
        }

        // ---- HLOD crossfade dissolveFloor: raises every prop's minimum dissolve (the whole-cluster fade-out seam) ----

        [Fact]
        public void DissolveFloor_Zero_IsByteIdenticalToNoFloor()
        {
            // A dissolveFloor of 0 must not perturb the old path: an in-range prop with no fade band stays solid.
            var placements = new List<PropPlacement> { new PropPlacement("pine_a", 30f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Meshes(("pine_a", 3)), Vector3.Zero, drawRadius: 100f, dissolveFloor: 0f);
            Assert.Single(si.Items);
            Assert.Equal(0f, si.Items[0].DissolveThreshold, 5);
        }

        [Fact]
        public void DissolveFloor_RaisesEveryPropDissolveUniformly()
        {
            // With no fade band, the floor is applied verbatim as each prop's dissolve (the whole cluster fades as one).
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine_a", 10f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("pine_a", 40f, 0f, 0f, 1f, 0f, 0),
            };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Meshes(("pine_a", 3)), Vector3.Zero, drawRadius: 100f, dissolveFloor: 0.4f);
            Assert.Equal(2, si.Items.Count);
            foreach (var it in si.Items) Assert.Equal(0.4f, it.DissolveThreshold, 4);   // uniform, distance-independent
        }

        [Fact]
        public void DissolveFloor_CombinesWithFadeBandByMax()
        {
            // Fade band [60,100] gives a prop at 80 a 0.5 dissolve. A floor of 0.7 wins (max), a floor of 0.2 loses.
            var high = new SceneInstances();
            PropRenderer.Queue(high, new List<PropPlacement> { new PropPlacement("pine_a", 80f, 0f, 0f, 1f, 0f, 0) },
                Meshes(("pine_a", 3)), Vector3.Zero, drawRadius: 100f, fadeBandWidth: 40f, dissolveFloor: 0.7f);
            Assert.Equal(0.7f, high.Items[0].DissolveThreshold, 4);

            var low = new SceneInstances();
            PropRenderer.Queue(low, new List<PropPlacement> { new PropPlacement("pine_a", 80f, 0f, 0f, 1f, 0f, 0) },
                Meshes(("pine_a", 3)), Vector3.Zero, drawRadius: 100f, fadeBandWidth: 40f, dissolveFloor: 0.2f);
            Assert.Equal(0.5f, low.Items[0].DissolveThreshold, 4);
        }
    }
}
