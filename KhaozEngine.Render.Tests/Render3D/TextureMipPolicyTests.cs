using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of the pure level arithmetic behind <see cref="TextureMipPolicy"/>. The upload it
    /// feeds is <see cref="Scene3D.LoadTexture(byte[],int,int,TextureMipPolicy)"/>, which only forwards the count.</summary>
    public sealed class TextureMipPolicyTests
    {
        [Fact]
        public void Default_IsFull()
        {
            // The whole point of the optional parameter: every existing caller passes nothing and must keep the full
            // chain it had before the type existed.
            Assert.Equal(TextureMipPolicy.Full, default);
            Assert.Equal(TextureMipPolicy.Full.LevelsFor(1024, 512), default(TextureMipPolicy).LevelsFor(1024, 512));
        }

        [Theory]
        [InlineData(1, 1, 1u)]
        [InlineData(4, 4, 3u)]
        [InlineData(256, 256, 9u)]
        [InlineData(1024, 512, 11u)]   // the longest side drives it
        public void Full_MatchesTheSplatChain(int w, int h, uint expected)
        {
            Assert.Equal(expected, TextureMipPolicy.Full.LevelsFor(w, h));
            Assert.Equal(SplatMaterialConfig.MipLevelCount(w, h), TextureMipPolicy.Full.LevelsFor(w, h));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(256, 256)]
        [InlineData(1024, 512)]
        public void None_IsLevelZeroOnly(int w, int h)
        {
            Assert.Equal(1u, TextureMipPolicy.None.LevelsFor(w, h));
        }

        [Fact]
        public void AtlasGrid_ConsumerSheet_StopsAtFourTexelCells()
        {
            // 1024x512 packed 4 across and 2 down is a 256-texel cell. Levels 0..6 keep the cell at 256..4 texels,
            // and level 7 would halve it to 2, so the chain is 7 levels deep instead of the full 11.
            Assert.Equal(7u, TextureMipPolicy.AtlasGrid(4, 2).LevelsFor(1024, 512));
            Assert.Equal(11u, TextureMipPolicy.Full.LevelsFor(1024, 512));
        }

        [Theory]
        [InlineData(4, 4, 256, 256)]
        [InlineData(4, 1, 512, 64)]
        public void AtlasGrid_follows_the_shorter_cell_side(int columns, int rows, int width, int height)
        {
            Assert.Equal(5u, TextureMipPolicy.AtlasGrid(columns, rows).LevelsFor(width, height));
        }

        [Fact]
        public void AtlasGrid_MinCellTexels_MovesTheFloor()
        {
            // A 256-texel cell: 16 texels leaves 5 levels, 4 texels leaves 7, and 1 texel runs to 9.
            Assert.Equal(5u, TextureMipPolicy.AtlasGrid(4, 2, minCellTexels: 16).LevelsFor(1024, 512));
            Assert.Equal(7u, TextureMipPolicy.AtlasGrid(4, 2, minCellTexels: 4).LevelsFor(1024, 512));
            Assert.Equal(9u, TextureMipPolicy.AtlasGrid(4, 2, minCellTexels: 1).LevelsFor(1024, 512));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-8)]
        public void AtlasGrid_MinCellTexelsBelowOne_ClampsToOne(int minCellTexels)
        {
            // A floor of 0 would loop until the shift produced 0, so it is guarded to 1 and matches minCellTexels 1.
            Assert.Equal(TextureMipPolicy.AtlasGrid(4, 2, 1).LevelsFor(1024, 512),
                TextureMipPolicy.AtlasGrid(4, 2, minCellTexels).LevelsFor(1024, 512));
        }

        [Theory]
        [InlineData(4, 4, 1, 1)]
        [InlineData(1, 1, 1, 1)]
        [InlineData(512, 512, 64, 64)]
        [InlineData(128, 1, 64, 64)]
        public void AtlasGrid_sub_floor_cells_keep_one_level(int columns, int rows, int width, int height)
        {
            Assert.Equal(1u, TextureMipPolicy.AtlasGrid(columns, rows).LevelsFor(width, height));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-4, -1)]
        public void AtlasGrid_NonPositiveGrid_ReadsAsOneCell(int columns, int rows)
        {
            // A degenerate grid must not divide by zero. One cell covering the whole sheet is the honest reading, so
            // the cap lands where a full-texture cell would put it.
            Assert.Equal(TextureMipPolicy.AtlasGrid(1, 1).LevelsFor(256, 256),
                TextureMipPolicy.AtlasGrid(columns, rows).LevelsFor(256, 256));
        }

        [Fact]
        public void AtlasGrid_NeverExceedsTheFullChain()
        {
            // A generous floor must not invent levels the texture does not have.
            foreach ((int w, int h) in new[] { (1, 1), (2, 2), (16, 16), (256, 128), (1024, 512) })
            {
                Assert.True(TextureMipPolicy.AtlasGrid(2, 2, 1).LevelsFor(w, h) <= TextureMipPolicy.Full.LevelsFor(w, h),
                    $"{w}x{h} asked for more levels than the full chain");
            }
        }

        [Fact]
        public void Equality_DistinguishesTheThreeShapes()
        {
            Assert.NotEqual(TextureMipPolicy.Full, TextureMipPolicy.None);
            Assert.NotEqual(TextureMipPolicy.Full, TextureMipPolicy.AtlasGrid(4, 2));
            Assert.NotEqual(TextureMipPolicy.AtlasGrid(4, 2), TextureMipPolicy.AtlasGrid(2, 4));
            Assert.NotEqual(TextureMipPolicy.AtlasGrid(4, 2), TextureMipPolicy.AtlasGrid(4, 2, 8));
            Assert.Equal(TextureMipPolicy.AtlasGrid(4, 2), TextureMipPolicy.AtlasGrid(4, 2, 4));
            Assert.True(TextureMipPolicy.Full == default);
            Assert.True(TextureMipPolicy.Full != TextureMipPolicy.None);
        }
    }
}
