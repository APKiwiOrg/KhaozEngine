using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    using Blobs = List<(Vector3 pos, float radius)>;

    /// <summary>
    /// Headless coverage of the blob-shadow seam inside <see cref="PropRenderer"/>'s internal Emit/EmitParts cull
    /// loop (issue #388): per-kit radius lookup, scale multiplication, the full-dissolve skip, and the
    /// null-table/null-sink no-op that keeps the seam inert by default. Exercises the <c>internal</c> Emit/EmitParts
    /// directly (KhaozEngine.Terrain.Render3D grants this assembly InternalsVisibleTo) with a fake blob sink, so none
    /// of this needs a real Scene3D or GPU. The mode-gating half (only Scene3D's DrawProps calls the blob sink when
    /// the resolved shadow tier is Blob) is covered by the GpuFact tests in PropRendererBlobGpuTests.
    /// </summary>
    public sealed class PropRendererBlobTests
    {
        static Dictionary<string, MeshHandle> Meshes(params (string id, int slot)[] entries)
        {
            var d = new Dictionary<string, MeshHandle>();
            foreach (var (id, slot) in entries) d[id] = new MeshHandle(slot);
            return d;
        }

        static Dictionary<string, IReadOnlyList<MeshHandle>> Parts(string id, params int[] slots)
        {
            var list = new List<MeshHandle>();
            foreach (int s in slots) list.Add(new MeshHandle(s));
            return new Dictionary<string, IReadOnlyList<MeshHandle>> { [id] = list };
        }

        // Emit/EmitParts are generic over their sink state (issue #393), so production can hand in a struct plus a
        // static delegate instead of allocating a closure per call. Here the state is just the list a blob lands in.
        static void NoOpSink(Blobs blobs, MeshHandle handle, Matrix4x4 world, float dissolve) { }
        static void RecordBlob(Blobs blobs, Vector3 pos, float radius) => blobs.Add((pos, radius));

        [Fact]
        public void No_blob_table_registers_nothing()
        {
            var placements = new List<PropPlacement> { new("pine_a", 2f, 0f, 3f, 2f, 0f, 0) };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, blobRadii: null, blobSink: RecordBlob);

            Assert.Empty(blobs);
        }

        [Fact]
        public void No_blob_sink_registers_nothing_even_with_a_table()
        {
            // A layer's BlobRadii table alone must not fire the sink: DrawProps only supplies a non-null sink when
            // the resolved shadow tier is Blob, so Emit must stay silent with a table but no sink too.
            var placements = new List<PropPlacement> { new("pine_a", 2f, 0f, 3f, 2f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new Blobs();

            int count = PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, blobRadii: radii, blobSink: null);

            Assert.Equal(1, count);   // the prop itself still draws
        }

        [Fact]
        public void Kit_with_a_radius_entry_registers_scaled_by_placement_scale()
        {
            var placements = new List<PropPlacement> { new("pine_a", 2f, 5f, 3f, 2.5f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            var (pos, radius) = Assert.Single(blobs);
            Assert.Equal(new Vector3(2f, 5f, 3f), pos);      // the placement's ground position
            Assert.Equal(1.5f * 2.5f, radius, 4);            // base radius x placement scale
        }

        [Fact]
        public void Kit_absent_from_the_table_registers_no_blob()
        {
            var placements = new List<PropPlacement> { new("rock_a", 2f, 0f, 3f, 1f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };   // rock_a has no entry
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("rock_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Empty(blobs);
        }

        [Fact]
        public void Out_of_range_placement_registers_no_blob()
        {
            var placements = new List<PropPlacement> { new("pine_a", 500f, 0f, 0f, 1f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            int count = PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Equal(0, count);
            Assert.Empty(blobs);
        }

        [Fact]
        public void Unknown_mesh_id_registers_no_blob_even_with_a_radius_entry()
        {
            // A kit id with a blob radius but no mesh handle never draws, so it must not leave a blob behind either.
            var placements = new List<PropPlacement> { new("ghost_a", 2f, 0f, 3f, 1f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["ghost_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Empty(blobs);
        }

        [Fact]
        public void Fully_dissolved_placement_in_the_fade_band_registers_no_blob()
        {
            // At exactly the draw radius the dissolve ramps to 1 (fully discarded): the prop is invisible there, so
            // it must not leave a floating blob with no visible caster.
            var placements = new List<PropPlacement> { new("pine_a", 40f, 0f, 0f, 1f, 0f, 0) };   // AT drawRadius
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 12f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Empty(blobs);
        }

        [Fact]
        public void Partially_dissolved_placement_still_registers_a_blob()
        {
            // Inside the fade band but not yet fully discarded (dissolve < 1): the prop still draws (fading), so it
            // still gets its blob.
            var placements = new List<PropPlacement> { new("pine_a", 30f, 0f, 0f, 1f, 0f, 0) };   // inside the band
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 12f, 0f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Single(blobs);
        }

        [Fact]
        public void HlodDissolveFloor_of_one_registers_no_blob()
        {
            // The HLOD crossfade drives dissolveFloor to 1 when a chunk cluster has fully swapped to its merged
            // mesh: Emit must treat that exactly like the fade band's full dissolve (no blob for a prop the HLOD
            // branch is no longer drawing individually).
            var placements = new List<PropPlacement> { new("pine_a", 2f, 0f, 3f, 1f, 0f, 0) };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();

            PropRenderer.Emit(placements, Meshes(("pine_a", 1)), null, 0f, Vector3.Zero, 40f, 0f, 1f,
                blobs, NoOpSink, radii, RecordBlob);

            Assert.Empty(blobs);
        }

        [Fact]
        public void EmitParts_registers_exactly_one_blob_per_placement_not_per_part()
        {
            var placements = new List<PropPlacement> { new("pine_a", 2f, 5f, 3f, 2f, 0f, 0) };
            var parts = Parts("pine_a", 1, 2, 3);   // three sub-mesh parts, one placement
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var blobs = new List<(Vector3 pos, float radius)>();
            int partSinkCalls = 0;

            int count = PropRenderer.EmitParts(placements, parts, null, 0f, Vector3.Zero, drawRadius: 40f,
                fadeBandWidth: 0f, dissolveFloor: 0f,
                state: blobs, sink: (_, handle, world, dissolve) => partSinkCalls++,
                blobRadii: radii, blobSink: RecordBlob);

            Assert.Equal(1, count);
            Assert.Equal(3, partSinkCalls);      // every part still instances
            var (pos, radius) = Assert.Single(blobs);
            Assert.Equal(new Vector3(2f, 5f, 3f), pos);
            Assert.Equal(3.0f, radius, 4);
        }
    }
}
