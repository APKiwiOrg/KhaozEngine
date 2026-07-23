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

        [Fact]
        public void FadeBand_Zero_KeepsHardCut_NoDissolve()
        {
            // Band 0 = today's behaviour: an in-range prop carries no dissolve (the byte-identical old path).
            Assert.Equal(0f, DissolveAtDistance(dist: 50f, drawRadius: 100f, fadeBandWidth: 0f), 5);
            Assert.Equal(0f, DissolveAtDistance(dist: 99f, drawRadius: 100f, fadeBandWidth: 0f), 5);
        }

        [Fact]
        public void FadeBand_InsideInnerRadius_NoDissolve()
        {
            // drawRadius 100, band 40 -> fade starts at 60. A prop at 50 is fully solid.
            Assert.Equal(0f, DissolveAtDistance(dist: 50f, drawRadius: 100f, fadeBandWidth: 40f), 5);
            Assert.Equal(0f, DissolveAtDistance(dist: 60f, drawRadius: 100f, fadeBandWidth: 40f), 5);   // exactly at the inner edge
        }

        [Fact]
        public void FadeBand_AcrossBand_RampsZeroToOne_ByDistance()
        {
            // fade band [60,100]: 80 is the midpoint (0.5), 100 is fully dissolved (1). Deterministic per distance.
            Assert.Equal(0.5f, DissolveAtDistance(dist: 80f, drawRadius: 100f, fadeBandWidth: 40f), 4);
            Assert.Equal(0.75f, DissolveAtDistance(dist: 90f, drawRadius: 100f, fadeBandWidth: 40f), 4);
            Assert.Equal(1f, DissolveAtDistance(dist: 100f, drawRadius: 100f, fadeBandWidth: 40f), 4);
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
        public void FadeBand_WiderThanRadius_Clamped_StartsAtFocus_ReachesOneAtRadius()
        {
            // Band 200 > radius 100: clamped so the fade starts at the focus (dist 0 -> 0) and still reaches 1 at the
            // radius, never dissolving a prop at the focus.
            Assert.Equal(0f, DissolveAtDistance(dist: 0f, drawRadius: 100f, fadeBandWidth: 200f), 4);
            Assert.Equal(0.5f, DissolveAtDistance(dist: 50f, drawRadius: 100f, fadeBandWidth: 200f), 4);
            Assert.Equal(1f, DissolveAtDistance(dist: 100f, drawRadius: 100f, fadeBandWidth: 200f), 4);
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

        [Fact]
        public void Lod_WithinDistance_UsesFullMesh()
        {
            int slot = MeshSlotAtDistance(dist: 50f, lodDistance: 100f, Meshes(("pine_a", 7)), Meshes(("pine_a", 50)));
            Assert.Equal(7, slot);   // near: full mesh
        }

        [Fact]
        public void Lod_BeyondDistance_SelectsVariant()
        {
            int slot = MeshSlotAtDistance(dist: 150f, lodDistance: 100f, Meshes(("pine_a", 7)), Meshes(("pine_a", 50)));
            Assert.Equal(50, slot);  // far: LOD variant
        }

        [Fact]
        public void Lod_NoVariantForKit_FallsBackToFullMesh()
        {
            // The LOD set has a variant for a DIFFERENT kit, so this kit keeps its full mesh even out past lodDistance.
            int slot = MeshSlotAtDistance(dist: 150f, lodDistance: 100f, Meshes(("pine_a", 7)), Meshes(("rock_a", 50)));
            Assert.Equal(7, slot);
        }

        [Fact]
        public void Lod_ZeroDistance_NeverSwitches()
        {
            // lodDistance 0 disables switching: every prop draws its full mesh regardless of distance.
            int slot = MeshSlotAtDistance(dist: 300f, lodDistance: 0f, Meshes(("pine_a", 7)), Meshes(("pine_a", 50)));
            Assert.Equal(7, slot);
        }

        [Fact]
        public void Lod_NullVariants_NeverSwitches()
        {
            // No LOD set at all: unchanged behaviour, full mesh at every distance.
            int slot = MeshSlotAtDistance(dist: 300f, lodDistance: 100f, Meshes(("pine_a", 7)), lod: null);
            Assert.Equal(7, slot);
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
