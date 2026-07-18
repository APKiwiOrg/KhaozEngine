using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PropMaterialPresetsTests
    {
        [Fact]
        public void Procedural_ProducesAlbedoAndNormalOfExpectedSize()
        {
            GltfMaterialMaps maps = PropMaterialPresets.Procedural(size: 32);

            Assert.True(maps.Albedo.HasValue);
            Assert.True(maps.Normal.HasValue);
            Assert.False(maps.IsEmpty);
            Assert.Equal(32, maps.Albedo!.Value.Width);
            Assert.Equal(32, maps.Albedo!.Value.Height);
            Assert.Equal(32 * 32 * 4, maps.Albedo!.Value.Rgba.Length);
            Assert.Equal(32 * 32 * 4, maps.Normal!.Value.Rgba.Length);
        }

        [Fact]
        public void Procedural_NormalMapIsZDominant()
        {
            GltfMaterialMaps maps = PropMaterialPresets.Procedural(size: 16);
            byte[] n = maps.Normal!.Value.Rgba;
            for (int i = 0; i < n.Length; i += 4)
                Assert.True(n[i + 2] >= 200, "tangent-space normal B (z) should dominate");
        }

        [Fact]
        public void Procedural_IsDeterministic()
        {
            byte[] a = PropMaterialPresets.Procedural(size: 24).Albedo!.Value.Rgba;
            byte[] b = PropMaterialPresets.Procedural(size: 24).Albedo!.Value.Rgba;
            Assert.Equal(a, b);
        }
    }
}
