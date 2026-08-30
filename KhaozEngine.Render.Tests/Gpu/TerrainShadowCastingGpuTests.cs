using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the opt-in terrain caster (issue #280): with <see cref="Scene3D.TerrainCastsShadows"/> off a
    /// splat-terrain ridge throws no shadow at all, and with it on the ground behind the ridge is shaded.
    /// Pixel-presence, NOT golden: one deterministic scene is rendered twice and the same ground points are compared
    /// between the two renders and against open lit ground, so the thresholds are backend-agnostic and nothing is
    /// baked. That matters here more than usual, because the flag ships OFF and no existing golden may move.
    /// <para>
    /// The scene is the shape the issue describes, shrunk to a single chunk: a flat field carrying one gaussian
    /// ridge, lit by a shallow key light so the ridge throws a long shadow across the flat ground behind it. The
    /// ridge is pierced by a PASS (<see cref="RidgeFeature"/>'s gate), which is what gives the test its control:
    /// ground inside the pass has no wall upwind of it, so it stays lit in both renders and is the reference every
    /// ratio is taken against. A box prop stands in the pass and casts in both renders, which is the framing guard
    /// that the depth pass really is running with the flag off (otherwise "terrain casts nothing" would be
    /// indistinguishable from "shadows are off").
    /// </para>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class TerrainShadowCastingGpuTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public TerrainShadowCastingGpuTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        const int W = 480, H = 320;

        // Shallow key light travelling toward +z and down: a caster of height h throws its shadow 1.74 * h along +z.
        static readonly Vector3 Light = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));

        // One 60 m chunk centred on the origin, meshed at the densest tier so the ridge is real geometry.
        static readonly TerrainChunkRegion Region = new() { OriginX = -30f, OriginZ = -30f, Size = 60f };

        const float RidgeZ = -8f, RidgeHeight = 14f, RidgeWidth = 3.5f;
        // The pass: the wall is gated to zero within 7 m of x = 18 and back to full by 14 m, so x in [11, 25] is open
        // sky and everything at x <= 4 is a solid 14 m wall.
        const float PassAlong = 18f, PassWidth = 14f;

        static TerrainField Field() => new(new TerrainConfig
        {
            Seed = 1,
            // A perfectly flat base: no gentle roll, and the default single biome band contributes no hill
            // amplitude, so the ONLY height in this field is the ridge. Any shading difference is therefore the
            // ridge's shadow and not terrain noise shading itself.
            GentleAmplitude = 0f,
            DetailOctaves = 0,
            Features = new ITerrainFeature[]
            {
                new RidgeFeature(new Vector2(0f, RidgeZ), new Vector2(1f, 0f),
                    RidgeHeight, RidgeWidth, PassAlong, PassWidth),
            },
        });

        // Ground well behind the ridge and well inside its 24 m shadow reach, at x values where the wall is full
        // height. The nearest is 12 m downwind of the crest, so the terrain under every probe is under 5 cm tall:
        // these read the SHADOW, never the ridge itself.
        static readonly Vector2[] ShadowProbes =
        {
            new(-8f, 4f), new(-4f, 4f), new(0f, 4f),
            new(-8f, 7f), new(-4f, 7f), new(0f, 7f),
            new(-8f, 10f), new(-4f, 10f), new(0f, 10f),
        };

        // Open ground inside the pass: no wall upwind, so it is lit in both renders.
        static readonly Vector2 LitRef = new(18f, 7f);
        // Ground just downwind of the box prop standing in the pass: shaded by the PROP in both renders.
        static readonly Vector2 PropShadowProbe = new(18f, 0.5f);

        static readonly Matrix4x4 PropXform =
            Matrix4x4.CreateScale(1.6f, 2.4f, 1.6f) * Matrix4x4.CreateTranslation(18f, 1.2f, -2f);
        static readonly Color PropColor = new(0.2f, 0.75f, 0.25f, 1f);

        static readonly Vector3 CamTarget = new(4f, 0f, 2f);
        static readonly Vector3 CamExtent = new(34f, 16f, 30f);
        const float CamAz = 0.35f, CamEl = 0.95f;

        /// <summary>Render the ridge scene once, with the terrain caster flag as given, and report how many rigid
        /// caster instances the depth pass considered.</summary>
        static byte[] Render(bool terrainCasts, out int casterCandidates)
        {
            var field = Field();
            var chunk = TerrainChunkBuilder.Build(field, Region, lod: 0);
            MeshHandle terrain = default, prop = default;
            int candidates = 0;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(FlatMaterial());
                    terrain = scene.LoadTerrainChunk(chunk, mat);
                    prop = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.TerrainCastsShadows = terrainCasts;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = Light;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Camera.Azimuth = CamAz;
                    scene.Camera.Elevation = CamEl;
                    scene.Camera.Frame(CamTarget, CamExtent);
                },
                drawFrame: scene =>
                {
                    // Read on entry, so the second frame's call reports what the FIRST frame's depth pass walked
                    // (the second frame is a dirty-skip and never rebuilds the list).
                    candidates = scene.ShadowCasterCandidateCount;
                    scene.DrawTerrainChunk(terrain, Region);
                    scene.Draw(prop, PropXform, PropColor);
                },
                frames: 2);
            casterCandidates = candidates;
            return rgba;
        }

        [GpuFact]
        public void Terrain_casts_only_when_the_scene_opts_in()
        {
            byte[] off = Render(terrainCasts: false, out int offCasters);
            byte[] on = Render(terrainCasts: true, out int onCasters);

            var field = Field();
            float offLit = Lum(off, field, LitRef);
            float onLit = Lum(on, field, LitRef);
            float offShadow = MeanLum(off, field, ShadowProbes);
            float onShadow = MeanLum(on, field, ShadowProbes);
            float offProp = Lum(off, field, PropShadowProbe);
            float onProp = Lum(on, field, PropShadowProbe);
            _out.WriteLine($"lit {offLit:0.####} -> {onLit:0.####}, ridge ground {offShadow:0.####} -> {onShadow:0.####}, " +
                           $"prop shadow {offProp:0.####} -> {onProp:0.####}, casters {offCasters} -> {onCasters}");

            // Framing guard first: the reference must be real lit ground in both renders, or every ratio below is
            // meaningless.
            Assert.True(offLit > 0.05f, $"the lit reference is not lit (luminance {offLit:0.####}); the camera or scene moved");

            // And the depth pass really is running with the flag OFF: the prop standing in the pass shades the
            // ground behind it in both renders. Without this, "terrain casts nothing" could just mean "no shadows".
            Assert.True(offProp < offLit * 0.85f,
                $"the prop cast no shadow with the flag off (probe {offProp:0.####} against lit {offLit:0.####}), " +
                "so this test is not measuring the terrain caster");
            Assert.True(onProp < onLit * 0.85f,
                $"the prop stopped casting with the flag on (probe {onProp:0.####} against lit {onLit:0.####})");

            // The defect: with the flag off, ground the ridge should shade reads as open lit ground.
            Assert.True(offShadow > offLit * 0.95f,
                $"the ridge already shaded the ground with the flag OFF (ground {offShadow:0.####} against lit " +
                $"{offLit:0.####}); the receive-only default must be pixel-unchanged");

            // The fix: with the flag on, the same ground is clearly darker. Absolute, so no golden is involved.
            Assert.True(onShadow < onLit * 0.8f,
                $"the ridge cast no shadow with the flag ON (ground {onShadow:0.####} against lit {onLit:0.####})");
            Assert.True(onShadow < offShadow * 0.85f,
                $"the flag barely changed the ground behind the ridge ({offShadow:0.####} -> {onShadow:0.####})");

            // Open ground stays open: the flag darkens what the ridge occludes, not the whole frame (which is what
            // a terrain self-shadow acne regression would look like).
            Assert.InRange(onLit, offLit * 0.95f, offLit * 1.05f);

            // And the terrain really entered the caster list rather than the shadow arriving some other way: one
            // chunk instance joins the one prop instance.
            Assert.Equal(1, offCasters);
            Assert.Equal(2, onCasters);
        }

        // ---- harness --------------------------------------------------------------------------------------------

        static IsoCamera3D Camera()
        {
            var cam = new IsoCamera3D { Azimuth = CamAz, Elevation = CamEl };
            cam.Frame(CamTarget, CamExtent);
            cam.AspectRatio = (float)W / H;
            return cam;
        }

        static float MeanLum(byte[] rgba, TerrainField field, Vector2[] points)
        {
            float sum = 0f;
            foreach (Vector2 p in points) sum += Lum(rgba, field, p);
            return sum / points.Length;
        }

        // Luminance of the ground at a world XZ, projected through the same camera the render used. The Y comes
        // from the field, so the probe lands on the surface rather than on the y = 0 plane under it.
        static float Lum(byte[] rgba, TerrainField field, Vector2 p)
        {
            var world = new Vector3(p.X, field.SampleHeight(p.X, p.Y), p.Y);
            if (!Camera().WorldToScreen(world, W, H, out Vector2 s)) return 0f;
            int px = (int)(s.X + 0.5f), py = (int)(s.Y + 0.5f);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            if (n == 0) return 0f;
            float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
            return 0.299f * rf + 0.587f * gf + 0.114f * bf;
        }

        // Five identical flat mid-grey layers, so the rendered ground is one colour whatever the baked splat blend
        // is and the only thing varying across the frame is the lighting.
        static TerrainLayeredMaterial FlatMaterial()
        {
            const int size = 8;
            byte[] albedo = new byte[size * size * 4];
            byte[] normal = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                int j = i * 4;
                albedo[j] = 150; albedo[j + 1] = 152; albedo[j + 2] = 145; albedo[j + 3] = 255;
                normal[j] = 128; normal[j + 1] = 128; normal[j + 2] = 255; normal[j + 3] = 255;   // tangent-space up
            }
            TerrainMaterialLayer Layer() => new()
            {
                AlbedoRgba = (byte[])albedo.Clone(),
                NormalRgba = (byte[])normal.Clone(),
                Tint = Color.White,
                TilesPerMetre = 0.25f,
                Roughness = 0.9f,
            };
            return new TerrainLayeredMaterial
            {
                Width = size, Height = size,
                Grass = Layer(), Dirt = Layer(), Rock = Layer(), Sand = Layer(), Snow = Layer(),
            };
        }
    }
}
