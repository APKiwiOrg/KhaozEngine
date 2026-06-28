using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SplatMaterialTests
    {
        [Fact]
        public void MipLevelCountIsFullChain()
        {
            Assert.Equal(1u, SplatMaterialConfig.MipLevelCount(1, 1));
            Assert.Equal(3u, SplatMaterialConfig.MipLevelCount(4, 4));
            Assert.Equal(9u, SplatMaterialConfig.MipLevelCount(256, 256));
            Assert.Equal(9u, SplatMaterialConfig.MipLevelCount(256, 128)); // max dimension drives it
        }

        [Fact]
        public void TriplanarBlendSumsToOneAndPicksDominantAxis()
        {
            var up = SplatMath.TriplanarBlend(new Vector3(0, 1, 0), 4f);
            Assert.True(up.Y > 0.99f);
            Assert.Equal(1f, up.X + up.Y + up.Z, 3);

            var side = SplatMath.TriplanarBlend(new Vector3(1, 0, 0), 4f);
            Assert.True(side.X > 0.99f);
        }

        [Fact]
        public void PlanarBlendIsXzOnly()
        {
            Assert.Equal(new Vector3(0, 1, 0), SplatMath.PlanarBlend());
        }

        [Fact]
        public void UnpackWeightsReconstructsFifthAndNormalizes()
        {
            // grass .4 dirt .1 rock .2 sand .1 -> snow .2, already normalized.
            var (g, d, r, s, snow) = SplatMath.UnpackWeights(new Vector4(0.4f, 0.1f, 0.2f, 0.1f));
            Assert.Equal(0.2f, snow, 4);
            Assert.Equal(1f, g + d + r + s + snow, 4);
        }

        [Fact]
        public void BuildParamsPacksPerLayerScalarsAndGlobals()
        {
            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < 5; i++)
                layers.Add(new SplatLayerImage { Tint = Color.White, TilesPerMetre = 0.1f * (i + 1), Roughness = 0.1f * i });
            var p = SplatMaterialConfig.BuildParams(layers, triplanarSharpness: 6f, projection: SplatProjection.PlanarXz, baseSpecStrength: 0.2f);

            Assert.Equal(0.1f, p.TintTiling0.W, 4);          // layer 0 tiling
            Assert.Equal(0.5f, p.TintTiling4.W, 4);          // layer 4 tiling
            Assert.Equal(0.0f, p.Roughness.X, 4);            // layer 0 roughness
            Assert.Equal(0.3f, p.Roughness.W, 4);            // layer 3 roughness
            Assert.Equal(0.4f, p.Misc.X, 4);                 // layer 4 roughness
            Assert.Equal(6f, p.Misc.Y, 4);                   // triplanar sharpness
            Assert.Equal(1f, p.Misc.Z, 4);                   // PlanarXz == 1
            Assert.Equal(0.2f, p.Misc.W, 4);                 // base spec
        }

        [Fact]
        public void ParamsDataIs112Bytes()
        {
            Assert.Equal(112, (int)SplatParamsData.SizeInBytes);
            Assert.Equal(112, System.Runtime.InteropServices.Marshal.SizeOf<SplatParamsData>());
        }
    }
}
