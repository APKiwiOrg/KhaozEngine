using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class IconAtlasTests
    {
        [Fact]
        public void Bake_HasExpectedDimensions()
        {
            var (px, w, h, uvs) = IconAtlas.BakeAtlasPixels(cell: 32);
            Assert.Equal(w * h * 4, px.Length);
            // 15 core icons packed into a grid of 32px cells; atlas is a whole number of cells.
            Assert.True(w % 32 == 0 && h % 32 == 0);
            Assert.Equal(Icons.All.Count, uvs.Count);
        }

        [Fact]
        public void Bake_EveryCoreIconHasNonTrivialAlphaCoverage()
        {
            var (px, w, h, uvs) = IconAtlas.BakeAtlasPixels(cell: 32);
            foreach (string id in Icons.All)
            {
                Vector4 uv = uvs[id];
                int x0 = (int)(uv.X * w), y0 = (int)(uv.Y * h);
                int x1 = (int)(uv.Z * w), y1 = (int)(uv.W * h);
                long opaque = 0, total = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        total++;
                        if (px[(y * w + x) * 4 + 3] > 40) opaque++;
                    }
                Assert.True(opaque > total / 100, $"icon '{id}' alpha coverage too low ({opaque}/{total})");
                Assert.True(opaque < total, $"icon '{id}' should not be fully opaque");
            }
        }

        [Fact]
        public void Bake_RgbIsWhiteEverywhere()
        {
            var (px, _, _, _) = IconAtlas.BakeAtlasPixels(cell: 16);
            for (int i = 0; i < px.Length; i += 4)
            {
                Assert.Equal(255, px[i]); Assert.Equal(255, px[i + 1]); Assert.Equal(255, px[i + 2]);
            }
        }

        [Fact]
        public void Register_CustomId_ResolvesThroughTryGetAndHas()
        {
            var atlas = new IconAtlas();
            var uv = new Vector4(0.1f, 0.2f, 0.7f, 0.9f);

            Assert.False(atlas.Has("game.fireball"));
            Assert.False(atlas.TryGet("game.fireball", out _, out _));

            // A game registers its own icon id against its own texture. Null stands in for that game texture handle:
            // the registry stores and returns the (texture, uv) verbatim, and it is the LOOKUP (not the pixels) under
            // test - this custom-id path is otherwise only exercised by the baked core set.
            atlas.Register("game.fireball", null!, uv);

            Assert.True(atlas.Has("game.fireball"));
            Assert.True(atlas.TryGet("game.fireball", out Texture2D tex, out Vector4 got));
            Assert.Null(tex);          // the stored handle round-trips verbatim
            Assert.Equal(uv, got);     // the stored source UV round-trips exactly
        }
    }
}
