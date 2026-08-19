using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The tile-ground params packing, headless. <c>Scene3D.LoadTileGroundMaterial</c>'s own argument checks need a
    /// device to reach, so the packing is what a GPU-free test can pin: the tail is exactly what the GLSL block
    /// declares (64 TintTiling entries then Misc), each layer lands at its own slot index, tint rides in xyz and
    /// tiles-per-metre in w, and the entries past the layer count stay ZERO so a mesh naming a slot the set never
    /// filled renders black instead of quietly borrowing another material's look.
    /// </summary>
    public class TileGroundMaterialConfigTests
    {
        static TileGroundLayerImage Layer(Color tint, float tilesPerMetre) =>
            new() { AlbedoRgba = new byte[4], Tint = tint, TilesPerMetre = tilesPerMetre };

        [Fact]
        public void BuildParams_ReturnsTheDeclaredTailLength()
        {
            Vector4[] tail = TileGroundMaterialConfig.BuildParams(new[] { Layer(Color.White, 0.5f) }, 0.15f);

            Assert.Equal(TileGroundMaterialConfig.MaxMaterials + 1, tail.Length);
            Assert.Equal(64, TileGroundMaterialConfig.MaxMaterials);
            Assert.Equal(TileGroundMaterialConfig.ParamsBytes, (uint)(tail.Length * 16));
        }

        [Fact]
        public void BuildParams_PacksTintInRgbAndTilingInW_AtTheLayersOwnSlot()
        {
            var layers = new[]
            {
                Layer(new Color(1f, 0f, 0f, 1f), 0.25f),
                Layer(new Color(0f, 0.5f, 1f, 1f), 2f),
            };

            Vector4[] tail = TileGroundMaterialConfig.BuildParams(layers, 0.15f);

            Assert.Equal(new Vector4(1f, 0f, 0f, 0.25f), tail[0]);
            Assert.Equal(new Vector4(0f, 0.5f, 1f, 2f), tail[1]);
        }

        [Fact]
        public void BuildParams_ZeroesTheSlotsNoLayerFilled()
        {
            Vector4[] tail = TileGroundMaterialConfig.BuildParams(new[] { Layer(Color.White, 0.5f) }, 0.15f);

            for (int i = 1; i < TileGroundMaterialConfig.MaxMaterials; i++)
                Assert.Equal(Vector4.Zero, tail[i]);
        }

        [Fact]
        public void BuildParams_PutsBaseSpecStrengthInMiscX()
        {
            Vector4[] tail = TileGroundMaterialConfig.BuildParams(new[] { Layer(Color.White, 0.5f) }, 0.42f);

            Assert.Equal(TileGroundMaterialConfig.MaxMaterials, TileGroundMaterialConfig.MiscIndex);
            Assert.Equal(new Vector4(0.42f, 0f, 0f, 0f), tail[TileGroundMaterialConfig.MiscIndex]);
        }

        [Fact]
        public void BuildParams_RejectsAnEmptySetAndOneOverTheCeiling()
        {
            Assert.Throws<ArgumentException>(() =>
                TileGroundMaterialConfig.BuildParams(Array.Empty<TileGroundLayerImage>(), 0.15f));

            var tooMany = new TileGroundLayerImage[TileGroundMaterialConfig.MaxMaterials + 1];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = Layer(Color.White, 0.5f);
            Assert.Throws<ArgumentException>(() => TileGroundMaterialConfig.BuildParams(tooMany, 0.15f));
        }

        [Fact]
        public void MipLevelCount_IsTheFullChain()
        {
            Assert.Equal(1u, TileGroundMaterialConfig.MipLevelCount(1, 1));
            Assert.Equal(3u, TileGroundMaterialConfig.MipLevelCount(4, 4));
            Assert.Equal(9u, TileGroundMaterialConfig.MipLevelCount(256, 256));
        }

        [Fact]
        public void LayerDefaults_AreWhiteTintAndATwoMetreRepeat()
        {
            var layer = new TileGroundLayerImage();

            Assert.Equal(Color.White, layer.Tint);
            Assert.Equal(0.5f, layer.TilesPerMetre);
            Assert.Empty(layer.AlbedoRgba);
        }
    }
}
