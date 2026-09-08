using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class ShadowMapDetailTests
    {
        [Theory]
        [InlineData(ShadowMapDetail.Low, 1024)]
        [InlineData(ShadowMapDetail.Default, 2048)]
        [InlineData(ShadowMapDetail.High, 3072)]
        public void EveryDetailBuildsAFullDirectionalShadowMap(ShadowMapDetail detail, int resolution)
        {
            ShadowSettings settings = ShadowSettings.ForDetail(detail);
            Assert.Equal(ShadowMode.ShadowMap, settings.Mode);
            Assert.Equal(resolution, settings.ShadowMapResolution);
            Assert.Equal(3, settings.ShadowCascadeCount);
            Assert.Equal(130f, settings.ShadowMaxDistance);
        }

        [Fact]
        public void UnknownDetailFallsBackToDefault()
        {
            ShadowSettings settings = ShadowSettings.ForDetail((ShadowMapDetail)99);
            Assert.Equal(ShadowMode.ShadowMap, settings.Mode);
            Assert.Equal(2048, settings.ShadowMapResolution);
        }

        [Fact]
        public void FactoryReturnsFreshUncommittedSettings()
        {
            ShadowSettings first = ShadowSettings.ForDetail(ShadowMapDetail.Low);
            ShadowSettings second = ShadowSettings.ForDetail(ShadowMapDetail.Low);
            Assert.NotSame(first, second);
            first.ShadowMapResolution = 512;
            Assert.Equal(1024, second.ShadowMapResolution);
        }
    }
}
