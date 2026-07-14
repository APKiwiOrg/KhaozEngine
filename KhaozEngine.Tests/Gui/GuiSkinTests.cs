using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    // Pure nine-slice decomposition (no GPU, no texture): GuiDraw.NineSlicePatches / TileCount, plus the skin
    // flat-path pin. GuiSkin is built by hand here (Texture left null) so the geometry is exercised without a device;
    // NineSlicePatches never touches the texture.
    public class GuiSkinTests
    {
        static GuiSkin Skin(float inset, GuiSkinCenter center = GuiSkinCenter.Stretch, float srcPx = 32f) => new()
        {
            Source = new Vector4(0f, 0f, 1f, 1f),
            SourcePixelWidth = srcPx,
            SourcePixelHeight = srcPx,
            InsetLeft = inset,
            InsetTop = inset,
            InsetRight = inset,
            InsetBottom = inset,
            Center = center,
        };

        static IEnumerable<GuiDraw.NineSlicePatch> Corners(IEnumerable<GuiDraw.NineSlicePatch> p, float w, float h) =>
            p.Where(x => System.MathF.Abs(x.Dest.Width - w) < 1e-3f && System.MathF.Abs(x.Dest.Height - h) < 1e-3f);

        // ---- Stretch decomposition -------------------------------------------------------------------------

        [Fact]
        public void Stretch_produces_nine_cells_with_unscaled_corners_and_stretched_middle()
        {
            var dest = new Rect(0, 0, 100, 100);
            List<GuiDraw.NineSlicePatch> patches = GuiDraw.NineSlicePatches(dest, Skin(8f));

            Assert.Equal(9, patches.Count);

            // Four corners keep the source-pixel inset size (8x8): unscaled.
            Assert.Equal(4, Corners(patches, 8f, 8f).Count());

            // The centre cell stretches to fill the middle band, sourced from the middle UV window.
            GuiDraw.NineSlicePatch center = patches.Single(p =>
                System.MathF.Abs(p.Dest.X - 8f) < 1e-3f && System.MathF.Abs(p.Dest.Y - 8f) < 1e-3f);
            Assert.Equal(new Rect(8, 8, 84, 84), center.Dest);
            Assert.Equal(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), center.Source);

            // The nine cells tile the destination exactly (no gaps / no overrun).
            Assert.Equal(0f, patches.Min(p => p.Dest.X), 3);
            Assert.Equal(0f, patches.Min(p => p.Dest.Y), 3);
            Assert.Equal(100f, patches.Max(p => p.Dest.Right), 3);
            Assert.Equal(100f, patches.Max(p => p.Dest.Bottom), 3);
        }

        [Fact]
        public void Corner_source_uvs_map_the_pixel_insets()
        {
            List<GuiDraw.NineSlicePatch> patches = GuiDraw.NineSlicePatches(new Rect(0, 0, 100, 100), Skin(8f));
            // Top-left corner: dest (0,0,8,8), source (0,0, 8/32, 8/32) = (0,0,0.25,0.25).
            GuiDraw.NineSlicePatch tl = patches.Single(p => p.Dest.X == 0f && p.Dest.Y == 0f);
            Assert.Equal(new Vector4(0f, 0f, 0.25f, 0.25f), tl.Source);
        }

        // ---- Degenerate insets -----------------------------------------------------------------------------

        [Fact]
        public void Insets_larger_than_the_rect_clamp_the_destination_so_corners_meet()
        {
            // Width 10 but 8+8 of horizontal inset: destination insets scale to 5+5, corners meet at x=5, centre and
            // horizontal edges collapse to zero width and drop out.
            var dest = new Rect(0, 0, 10, 100);
            List<GuiDraw.NineSlicePatch> patches = GuiDraw.NineSlicePatches(dest, Skin(8f));

            Assert.Equal(6, patches.Count);                       // 2 columns x 3 rows, middle column gone
            Assert.All(patches, p => Assert.Equal(5f, p.Dest.Width, 3));
            Assert.Equal(5f, patches.Max(p => p.Dest.X), 3);      // right column starts where the left one ends
            Assert.Equal(10f, patches.Max(p => p.Dest.Right), 3);
        }

        [Fact]
        public void Zero_insets_collapse_to_a_single_whole_rect_patch()
        {
            var dest = new Rect(3, 4, 100, 60);
            List<GuiDraw.NineSlicePatch> patches = GuiDraw.NineSlicePatches(dest, Skin(0f));
            GuiDraw.NineSlicePatch only = Assert.Single(patches);
            Assert.Equal(dest, only.Dest);
            Assert.Equal(new Vector4(0f, 0f, 1f, 1f), only.Source);
        }

        // ---- Tile mode -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(84f, 16f, 6)]   // 5.25 tiles -> 6 (last partial)
        [InlineData(80f, 16f, 5)]   // exactly 5
        [InlineData(0f, 16f, 1)]    // no extent -> single span
        [InlineData(50f, 0f, 1)]    // degenerate tile size -> single span
        public void TileCount_ceilings_the_native_tile_fit(float extent, float tile, int expected)
        {
            Assert.Equal(expected, GuiDraw.TileCount(extent, tile));
        }

        [Fact]
        public void Tile_mode_repeats_the_middle_band_at_native_size_and_clips_the_last()
        {
            // dest 100x100, inset 8, src 32 -> native tile 16x16. Centre 84x84 -> 6x6 tiles; each edge tiles 6 along
            // its long axis; 4 corners. Total = 4 + (6+6) + (6+6) + 36 = 64.
            var dest = new Rect(0, 0, 100, 100);
            List<GuiDraw.NineSlicePatch> patches = GuiDraw.NineSlicePatches(dest, Skin(8f, GuiSkinCenter.Tile));
            Assert.Equal(64, patches.Count);

            // The trailing tile of a row is clipped: 84 = 5*16 + 4, so a 4-wide partial exists with its source U
            // clipped to a quarter of the tile window.
            GuiDraw.NineSlicePatch partial = patches.First(p =>
                System.MathF.Abs(p.Dest.Width - 4f) < 1e-3f && System.MathF.Abs(p.Dest.Y - 8f) < 1e-3f);
            // centre U window is [0.25,0.75]; a quarter of it from the left edge = 0.25 + 0.5*0.25 = 0.375.
            Assert.Equal(0.375f, partial.Source.Z, 3);
        }

        // ---- Flat-path pin: skin unset leaves everything as before -----------------------------------------

        [Fact]
        public void Default_styles_carry_no_skin_and_stay_on_their_existing_flat_flag()
        {
            Assert.Null(GuiStyle.Default.Skin);
            Assert.Null(GuiStyle.Legacy.Skin);
            Assert.True(GuiStyle.Legacy.IsFlat);   // unchanged: still the plain single-quad path
        }

        [Fact]
        public void Setting_a_skin_takes_the_style_off_the_flat_path()
        {
            var style = GuiStyle.Legacy;
            Assert.True(style.IsFlat);
            style.Skin = Skin(8f);
            Assert.False(style.IsFlat);   // a skin is never the flat quad path
        }
    }
}
