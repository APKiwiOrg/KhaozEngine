using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Which background the temporal resolve reprojects in place: the screen-fixed starfield takes zero motion
    /// through the resolve block's Params.y, while the sky and a solid clear keep the camera rotation reprojection.
    /// Headless, on <see cref="HeadlessSceneRig"/>, plus the maths and the shader text.
    /// </summary>
    public sealed class TemporalBackgroundMotionTests
    {
        // The rule in the per-texel preparation, which both entry points run.
        const string Rule = "if (surface.background && Params.y > 0.5) {";

        [Theory]
        [InlineData(BackgroundMode.Starfield, 1f)]
        [InlineData(BackgroundMode.Sky, 0f)]
        [InlineData(BackgroundMode.Solid, 0f)]
        public void OnlyTheStarfieldIsScreenFixed(BackgroundMode background, float flag)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Background = background;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            Assert.Equal(flag, rig.Scene.TemporalResolveRendererForTests!.LastUniforms.Params.Y);
        }

        [Fact]
        public void TheMathsCarriesTheFlagInParamsYAndChangesNothingElse()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, 2f, 10f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 500f);
            var input = new TemporalViewInput(view, projection);
            TemporalResolveUniforms plain = TemporalResolveMath.BuildUniforms(input, input, Vector2.Zero, 96, 64, 96,
                64, historyValid: true);
            TemporalResolveUniforms screenFixed = TemporalResolveMath.BuildUniforms(input, input, Vector2.Zero, 96, 64,
                96, 64, historyValid: true, screenFixedBackground: true);
            Assert.Equal(Vector4.Zero, plain.Params);
            Assert.Equal(new Vector4(0f, 1f, 0f, 0f), screenFixed.Params);
            Assert.Equal(plain.BackgroundToPrevious, screenFixed.BackgroundToPrevious);
            Assert.Equal(plain.CurrentToPrevious, screenFixed.CurrentToPrevious);
            Assert.Equal(plain.PreviousProjection, screenFixed.PreviousProjection);
            Assert.Equal(plain.Sizes, screenFixed.Sizes);
            Assert.Equal(plain.Jitter, screenFixed.Jitter);
            Assert.Equal(plain.CurrentDepth, screenFixed.CurrentDepth);
        }

        [Fact]
        public void ThePreparationGivesAScreenFixedBackgroundZeroMotionAfterItsLastReprojectionChoice()
        {
            string prepare = ShaderSources.TemporalPrepareGlsl;
            int rule = prepare.IndexOf(Rule, StringComparison.Ordinal);
            int own = prepare.IndexOf("surface.background = centreIsBackground;", StringComparison.Ordinal);
            Assert.True(rule >= 0, "the preparation has no screen-fixed background rule");
            Assert.True(own >= 0 && rule > own,
                "the rule must follow the last choice of the surface a pixel reprojects by");
            Assert.Equal(rule, prepare.LastIndexOf(Rule, StringComparison.Ordinal));
            string body = prepare.Substring(rule, prepare.IndexOf('}', rule) - rule);
            Assert.Contains("surface.motion = vec2(0.0);", body, StringComparison.Ordinal);
            Assert.Contains("surface.background = false;", body, StringComparison.Ordinal);
        }

        [Fact]
        public void BothEntryPointsTakeTheRule()
        {
            Assert.Contains(Rule, ShaderSources.TemporalResolveFrag, StringComparison.Ordinal);
            Assert.Contains(Rule, ShaderSources.TemporalResolveHalfFrag, StringComparison.Ordinal);
            Assert.Contains(Rule, ShaderSources.TemporalPrepareFrag, StringComparison.Ordinal);
        }
    }
}
