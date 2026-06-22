using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SurfaceMapsTests
    {
        [Fact]
        public void Albedo_only_leaves_normal_and_roughness_invalid()
        {
            var maps = new Scene3D.SurfaceMaps(new Scene3D.TextureHandle(0)); // valid: stored as index+1
            Assert.True(maps.Albedo.IsValid);
            Assert.False(maps.Normal.IsValid);
            Assert.False(maps.Roughness.IsValid);
        }

        [Fact]
        public void Default_struct_has_all_invalid_handles()
        {
            var maps = default(Scene3D.SurfaceMaps);
            Assert.False(maps.Albedo.IsValid);
            Assert.False(maps.Normal.IsValid);
            Assert.False(maps.Roughness.IsValid);
        }
    }
}
