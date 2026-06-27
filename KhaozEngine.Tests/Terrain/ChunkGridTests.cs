using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class ChunkGridTests
    {
        const float Size = 60f;

        [Fact]
        public void CoordOf_floors_toward_negative_infinity()
        {
            Assert.Equal(new ChunkCoord(0, 0), ChunkGrid.CoordOf(0f, 0f, Size));
            Assert.Equal(new ChunkCoord(0, 0), ChunkGrid.CoordOf(59.9f, 0.1f, Size));
            Assert.Equal(new ChunkCoord(1, 2), ChunkGrid.CoordOf(60f, 120f, Size));
            Assert.Equal(new ChunkCoord(-1, -1), ChunkGrid.CoordOf(-0.1f, -1f, Size));   // floors down, not toward zero
            Assert.Equal(new ChunkCoord(-1, -2), ChunkGrid.CoordOf(-60f, -61f, Size));
        }

        [Fact]
        public void RegionOf_and_AreaOf_cover_the_chunk_with_half_open_tiling()
        {
            var c = new ChunkCoord(2, -3);
            TerrainChunkRegion region = ChunkGrid.RegionOf(c, Size);
            Assert.Equal(120f, region.OriginX);
            Assert.Equal(-180f, region.OriginZ);
            Assert.Equal(Size, region.Size);

            RectArea area = ChunkGrid.AreaOf(c, Size);
            Assert.Equal(120f, area.MinX);
            Assert.Equal(-180f, area.MinZ);
            Assert.Equal(180f, area.MaxX);
            Assert.Equal(-120f, area.MaxZ);

            // Adjacent chunk's area starts exactly where this one ends (no gap, no overlap).
            RectArea next = ChunkGrid.AreaOf(new ChunkCoord(3, -3), Size);
            Assert.Equal(area.MaxX, next.MinX);
        }

        [Fact]
        public void CenterOf_is_the_chunk_midpoint()
        {
            Vector2 center = ChunkGrid.CenterOf(new ChunkCoord(0, 0), Size);
            Assert.Equal(30f, center.X, 3);
            Assert.Equal(30f, center.Y, 3);
        }
    }
}
