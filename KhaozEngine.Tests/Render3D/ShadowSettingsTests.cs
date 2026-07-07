using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) coverage of the shadow-tier plumbing: mode default + degradation decision, blob parameter
    /// derivation (radius/alpha + height fade), and the blob->decal build. GPU output is covered by the
    /// scene3d_shadow_blob golden.
    /// </summary>
    public sealed class ShadowSettingsTests
    {
        static GpuCapabilities Caps() => new(clipSpaceYInverted: false, depthRangeZeroToOne: true);

        [Fact]
        public void Default_is_off()
        {
            var s = new ShadowSettings();
            Assert.Equal(ShadowMode.Off, s.Mode);
            var r = s.ResolveFor(Caps());
            Assert.Equal(ShadowMode.Off, r.Effective);
            Assert.False(r.Degraded);
        }

        [Fact]
        public void RenderQuality_defaults_shadows_off()
        {
            Assert.Equal(ShadowMode.Off, new RenderQuality().Shadows.Mode);
            Assert.Equal(ShadowMode.Off, new PixelPostProcessSettings().Quality.Shadows.Mode);
        }

        [Fact]
        public void Blob_resolves_unchanged()
        {
            var s = new ShadowSettings { Mode = ShadowMode.Blob };
            var r = s.ResolveFor(Caps());
            Assert.Equal(ShadowMode.Blob, r.Effective);
            Assert.False(r.Degraded);
            Assert.Equal("", r.Reason);
        }

        [Fact]
        public void ShadowMap_degrades_to_blob_until_wired()
        {
            var s = new ShadowSettings { Mode = ShadowMode.ShadowMap };
            var r = s.ResolveFor(Caps());
            Assert.Equal(ShadowMode.ShadowMap, r.Requested);
            Assert.Equal(ShadowMode.Blob, r.Effective);
            Assert.True(r.Degraded);
            Assert.NotEqual("", r.Reason);
        }

        [Fact]
        public void BlobFor_grounded_gives_full_footprint_and_alpha()
        {
            var s = new ShadowSettings { BlobOpacity = 0.5f };
            var (radius, alpha) = s.BlobFor(footprint: 2f, requestStrength: 1f, heightAboveGround: 0f);
            Assert.Equal(2f, radius, 3);
            Assert.Equal(0.5f, alpha, 3);
        }

        [Fact]
        public void BlobFor_scales_alpha_by_request_strength()
        {
            var s = new ShadowSettings { BlobOpacity = 0.8f };
            var (_, alpha) = s.BlobFor(2f, requestStrength: 0.5f, heightAboveGround: 0f);
            Assert.Equal(0.4f, alpha, 3);
        }

        [Fact]
        public void BlobFor_height_fade_shrinks_and_lightens()
        {
            var s = new ShadowSettings { BlobOpacity = 1f, BlobFadeHeight = 4f };
            var ground = s.BlobFor(2f, 1f, heightAboveGround: 0f);
            var mid = s.BlobFor(2f, 1f, heightAboveGround: 2f);
            // Rising: smaller radius AND lighter alpha than on the ground.
            Assert.True(mid.radius < ground.radius);
            Assert.True(mid.alpha < ground.alpha);
            Assert.True(mid.alpha > 0f);
        }

        [Fact]
        public void BlobFor_at_or_above_fade_height_vanishes()
        {
            var s = new ShadowSettings { BlobOpacity = 1f, BlobFadeHeight = 4f };
            Assert.Equal(0f, s.BlobFor(2f, 1f, heightAboveGround: 4f).alpha, 4);
            Assert.Equal(0f, s.BlobFor(2f, 1f, heightAboveGround: 9f).alpha, 4);
        }

        [Fact]
        public void BlobFor_fade_disabled_when_fadeheight_nonpositive()
        {
            var s = new ShadowSettings { BlobOpacity = 1f, BlobFadeHeight = 0f };
            var high = s.BlobFor(2f, 1f, heightAboveGround: 100f);
            Assert.Equal(2f, high.radius, 3);
            Assert.Equal(1f, high.alpha, 3);
        }

        [Fact]
        public void TryBuildDecal_grounded_builds_dark_alpha_circle()
        {
            var s = new ShadowSettings { BlobOpacity = 0.5f, BlobColor = new(0.02f, 0.02f, 0.03f, 1f) };
            var blob = new ShadowBlob(new Vector3(3f, 1.2f, -4f), groundY: 0.1f, radius: 1.5f);
            Assert.True(s.TryBuildDecal(blob, out var d));
            Assert.Equal(DecalShape.Circle, d.Shape);
            Assert.Equal(DecalBlend.Alpha, d.Blend);
            // Sits on the ground plane, at the caster's XZ.
            Assert.Equal(3f, d.Center.X, 3);
            Assert.Equal(0.1f, d.Center.Y, 3);
            Assert.Equal(-4f, d.Center.Z, 3);
            Assert.Equal(1.5f, d.Size.X, 3);          // footprint radius
            Assert.Equal(0.5f, d.FillColor.A, 3);     // opacity x strength
            Assert.True(d.FillColor.R < 0.1f);        // near-black fill => darkens the ground
            Assert.Equal(0f, d.OutlineColor.A, 3);    // no outline ring on a blob
        }

        [Fact]
        public void TryBuildDecal_returns_false_above_fade_height()
        {
            var s = new ShadowSettings { BlobFadeHeight = 4f };
            var blob = new ShadowBlob(Vector3.Zero, groundY: 0f, radius: 1.5f, strength: 1f, heightAboveGround: 5f);
            Assert.False(s.TryBuildDecal(blob, out _));
        }

        [Fact]
        public void ShadowBlob_ctor_defaults_full_strength_grounded()
        {
            var b = new ShadowBlob(new Vector3(1f, 2f, 3f), groundY: 0.5f, radius: 2f);
            Assert.Equal(1f, b.Strength, 3);
            Assert.Equal(0f, b.HeightAboveGround, 3);
        }
    }
}
