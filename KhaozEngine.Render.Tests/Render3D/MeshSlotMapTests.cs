using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of <see cref="MeshSlotMap"/>: the pure (GPU-free) slot allocator backing
    /// <see cref="Scene3D"/>'s mesh storage. Verifies distinct indices on load, generation-based invalidation on
    /// free, freed-index reuse with a fresh generation, double-free rejection, and the default-handle invariant.
    /// </summary>
    public class MeshSlotMapTests
    {
        [Fact]
        public void Load_N_GivesDistinctIndices_AllValid()
        {
            var map = new MeshSlotMap();
            int a = map.Alloc(out int ga);
            int b = map.Alloc(out int gb);
            int c = map.Alloc(out int gc);

            Assert.Equal(0, a);
            Assert.Equal(1, b);
            Assert.Equal(2, c);
            Assert.True(map.IsValid(a, ga));
            Assert.True(map.IsValid(b, gb));
            Assert.True(map.IsValid(c, gc));
            // Generations start at 1 (so a default handle, generation 0, is never valid).
            Assert.Equal(1, ga);
        }

        [Fact]
        public void Unload_InvalidatesThatHandle_OthersStayValid()
        {
            var map = new MeshSlotMap();
            int a = map.Alloc(out int ga);
            int b = map.Alloc(out int gb);

            map.Free(a, ga);

            Assert.False(map.IsValid(a, ga));   // stale: freed slot, generation no longer matches (also flagged free)
            Assert.True(map.IsValid(b, gb));    // untouched slot still live
        }

        [Fact]
        public void Reload_ReusesFreedIndex_WithNewGeneration()
        {
            var map = new MeshSlotMap();
            int a = map.Alloc(out int ga);
            map.Free(a, ga);

            int reused = map.Alloc(out int gReused);

            Assert.Equal(a, reused);            // freed index reused
            Assert.NotEqual(ga, gReused);       // but a NEW generation
            Assert.True(map.IsValid(reused, gReused));
            Assert.False(map.IsValid(a, ga));   // the OLD handle is still stale against the reused slot
        }

        [Fact]
        public void DoubleUnload_IsRejected()
        {
            var map = new MeshSlotMap();
            int a = map.Alloc(out int ga);
            map.Free(a, ga);

            Assert.Throws<ArgumentException>(() => map.Free(a, ga));
        }

        [Fact]
        public void DefaultHandle_IsInvalid()
        {
            var map = new MeshSlotMap();
            map.Alloc(out _);                   // make slot 0 live

            Assert.False(map.IsValid(0, 0));    // generation 0 (a default handle) is never valid
            Assert.False(map.IsValid(-1, 1));   // out-of-range index
            Assert.False(map.IsValid(99, 1));   // out-of-range index
        }

        [Fact]
        public void Free_BogusGeneration_IsRejected()
        {
            var map = new MeshSlotMap();
            int a = map.Alloc(out int ga);

            Assert.Throws<ArgumentException>(() => map.Free(a, ga + 7));  // wrong generation
        }
    }
}
