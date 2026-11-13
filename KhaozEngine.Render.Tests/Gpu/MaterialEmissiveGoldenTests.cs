using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// glTF emissive (issue #1776): a material's emissive colour is HDR light added after lighting that the scene's
    /// lights and shadows never scale, on rigid and GPU-skinned meshes alike, and it feeds bloom. The property facts
    /// read absolute pixel differences, so they hold on every backend with no committed grid. The golden pins the
    /// look of a glTF-loaded emissive texture, a flat glow and a skinned glow under HDR bloom.
    /// </summary>
    public sealed class MaterialEmissiveGoldenTests(ITestOutputHelper output)
    {
        const int W = 192, H = 128;
        static readonly Vector3 Glow = new(0.30f, 0.10f, 0.20f);
        static readonly Color FloorGrey = new(0.45f, 0.45f, 0.45f, 1f);

        // The key light travels down and toward -X, so the unit box's shadow falls on the floor beside its -X face.
        static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.8f, -1f, 0.2f));
        static readonly Vector3 BoxCentre = new(0f, 0.5f, 0f);
        static readonly Vector3 ShadowPoint = new(-0.95f, 0.05f, 0.1f);   // on the floor, in the box's shadow
        static readonly Vector3 LitPoint = new(1.6f, 0.05f, -1.2f);       // on the floor, clear of box and shadow
        // The skinned tube lies along +X on the floor from TubeOrigin. TubeTop is a point on its upper surface.
        static readonly Vector3 TubeOrigin = new(0.6f, 0.3f, 1.4f);
        static readonly Vector3 TubeTop = new(1.2f, 0.55f, 1.4f);

        static IsoCamera3D Camera()
        {
            var camera = new IsoCamera3D { Azimuth = 0.45f, Elevation = 1.0f };
            camera.Frame(new Vector3(0f, 0f, 0.2f), new Vector3(5f, 1.5f, 4.5f));
            camera.AspectRatio = (float)W / H;
            return camera;
        }

        // The floor and the tube glow when emissive is on. The box never glows and only casts the shadow.
        static byte[] Capture(bool lit, bool emissive)
        {
            MeshHandle floor = default, box = default;
            SkinnedMeshHandle tube = default;
            SkinnedGltfMesh tubeMesh = SkinnedMeshBuilder.BuildTube(0.25f, 1.2f, 10, 8, 2, Axis.X);
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    Vector3 glow = emissive ? Glow : Vector3.Zero;
                    var maps = new Scene3D.SurfaceMaps(default, default, default, 0f, default, glow);
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f), maps);
                    box = scene.LoadMesh(MeshPrimitives.Box(1f));
                    tube = scene.LoadSkinnedMesh(tubeMesh, new GltfMaterialMaps(null, null, null, 0f, null, glow));
                    ConfigureFlat(scene, lit);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    IsoCamera3D camera = Camera();
                    scene.Camera.Azimuth = camera.Azimuth;
                    scene.Camera.Elevation = camera.Elevation;
                    scene.Camera.Frame(new Vector3(0f, 0f, 0.2f), new Vector3(5f, 1.5f, 4.5f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, FloorGrey);
                    scene.Draw(box, Matrix4x4.CreateTranslation(BoxCentre), new Color(0.3f, 0.3f, 0.3f, 1f));
                    scene.DrawSkinned(tube, tubeMesh.RestPose, Matrix4x4.CreateTranslation(TubeOrigin), FloorGrey);
                },
                frames: 2);
        }

        // HDR off (no tonemap), every stylized pass off, no fog. lit=false blacks out every light, so only emissive is left.
        static void ConfigureFlat(Scene3D scene, bool lit)
        {
            scene.Post.UseSmoothPreset();
            scene.Post.Hdr.Enabled = false;
            scene.Post.TransparentBackground = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.LightDirection = Light;
            scene.Post.LightColor = lit ? new Color(0.55f, 0.55f, 0.55f, 1f) : new Color(0f, 0f, 0f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = lit ? new Color(0.08f, 0.08f, 0.08f, 1f) : new Color(0f, 0f, 0f, 1f);
        }

        static Vector3 PixelAt(byte[] rgba, Vector3 world)
        {
            Assert.True(Camera().WorldToScreen(world, W, H, out Vector2 screen), $"{world} is off screen");
            int x = Math.Clamp((int)screen.X, 0, W - 1), y = Math.Clamp((int)screen.Y, 0, H - 1);
            int i = (y * W + x) * 4;
            return new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]);
        }

        [GpuFact]
        public void Emissive_IsLightThatLightsAndShadowsDoNotScale_OnRigidAndSkinned_Golden()
        {
            byte[] litGlow = Capture(lit: true, emissive: true), litNone = Capture(lit: true, emissive: false);
            byte[] darkGlow = Capture(lit: false, emissive: true), darkNone = Capture(lit: false, emissive: false);

            // The scene really is lit and really is shadowed, or equal deltas would prove nothing.
            Vector3 litFloor = PixelAt(litNone, LitPoint), shadowFloor = PixelAt(litNone, ShadowPoint);
            Assert.True(litFloor.X > PixelAt(darkNone, LitPoint).X + 40, $"key light not reaching the floor: {litFloor}");
            Assert.True(shadowFloor.X < litFloor.X - 20, $"no shadow at the shadow point: {shadowFloor} against {litFloor}");

            Vector3 expected = Glow * 255f;
            foreach ((string what, Vector3 point) in new[]
            {
                ("lit floor", LitPoint), ("shadowed floor", ShadowPoint), ("skinned tube", TubeTop),
            })
            {
                Vector3 lit = PixelAt(litGlow, point) - PixelAt(litNone, point);
                Vector3 dark = PixelAt(darkGlow, point) - PixelAt(darkNone, point);
                output.WriteLine($"{what}: emissive adds {lit} lit and {dark} dark, expected {expected}");
                Assert.True(Vector3.Distance(lit, expected) <= 3f, $"{what} lit: emissive added {lit}, expected {expected}");
                Assert.True(Vector3.Distance(dark, expected) <= 3f, $"{what} dark: emissive added {dark}, expected {expected}");
            }
        }

        [GpuFact]
        public void Emissive_FeedsBloomUnderHdr_Golden()
        {
            static byte[] Bloomed(bool emissive)
            {
                MeshHandle quad = default;
                return Render3DSnapshot.Capture(W, H,
                    setup: scene =>
                    {
                        Vector3 glow = emissive ? new Vector3(4f, 2f, 1f) : Vector3.Zero;
                        quad = scene.LoadMesh(MeshPrimitives.Tile(1f, 0.05f),
                            new Scene3D.SurfaceMaps(default, default, default, 0f, default, glow));
                        ConfigureFlat(scene, lit: false);
                        scene.Post.Hdr.Enabled = true;
                        scene.Post.Bloom.Enabled = true;
                        // At the capture's own size, so the bloom blur is measured in the pixels the ring reads
                        // rather than shrunk by the default 1600x900 internal target's downsample.
                        scene.Post.RenderScale = RenderScale.MatchViewport;
                        IsoCamera3D camera = Camera();
                        scene.Camera.Azimuth = camera.Azimuth;
                        scene.Camera.Elevation = camera.Elevation;
                        scene.Camera.Frame(new Vector3(0f, 0f, 0.2f), new Vector3(5f, 1.5f, 4.5f));
                    },
                    drawFrame: scene => scene.Draw(quad, Matrix4x4.Identity, FloorGrey),
                    frames: 2);
            }

            byte[] glow = Bloomed(emissive: true), none = Bloomed(emissive: false);
            // Points just outside the middle of each edge of the unit quad: geometry never covers them, so only bloom
            // can light them.
            Vector3[] ring = [new(0.65f, 0f, 0f), new(-0.65f, 0f, 0f), new(0f, 0f, 0.65f), new(0f, 0f, -0.65f)];
            double halo = ring.Average(p => Luma(PixelAt(glow, p)) - Luma(PixelAt(none, p)));
            output.WriteLine($"mean ring luma gained from the emissive quad's bloom: {halo:0.00}");
            Assert.True(PixelAt(none, ring[0]) == Vector3.Zero, "the dark scene is not black outside the quad");
            Assert.True(halo > 8.0, $"an HDR emissive must bloom past its silhouette, ring gained {halo:0.00}");
        }

        static double Luma(Vector3 rgb) => 0.2126 * rgb.X + 0.7152 * rgb.Y + 0.0722 * rgb.Z;

        // A glTF-loaded emissive texture, a flat glow and a skinned glow beside an unlit grey reference, under the
        // default HDR chain with bloom. Pins the composed look, including the texture's pattern through the factor.
        [GpuFact]
        public void Golden3D_GltfEmissive()
        {
            const int GW = 320, GH = 200;
            using var dir = new TempDir();
            byte[] stripes = Stripes();
            string glowing = MaterialGlbFixture.Write(dir.Path, "lantern.glb", new MaterialBuilder("lantern")
                .WithBaseColor(new Vector4(0.25f, 0.22f, 0.2f, 1f))
                .WithEmissive(new SharpGLTF.Memory.MemoryImage(stripes), new Vector3(1f, 0.6f, 0.25f), 3f));
            IReadOnlyList<GltfMeshPart> lantern = GltfLoader.LoadPartsWithMaterials(glowing);
            SkinnedGltfMesh tubeMesh = SkinnedMeshBuilder.BuildTube(0.3f, 1.6f, 12, 10, 3, Axis.X);
            Scene3D.PropHandle prop = default;
            MeshHandle flat = default, reference = default;
            SkinnedMeshHandle tube = default;

            byte[] rgba = Render3DSnapshot.Capture(GW, GH,
                setup: scene =>
                {
                    prop = scene.LoadProp(lantern);
                    flat = scene.LoadMesh(MeshPrimitives.Box(0.8f),
                        new Scene3D.SurfaceMaps(default, default, default, 0f, default, new Vector3(0.2f, 0.9f, 1.4f)));
                    reference = scene.LoadMesh(MeshPrimitives.Box(0.8f));
                    tube = scene.LoadSkinnedMesh(tubeMesh,
                        new GltfMaterialMaps(null, null, null, 0f, null, new Vector3(1.6f, 0.3f, 0.9f)));
                    scene.Post.UseSmoothPreset();
                    scene.Post.TransparentBackground = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.02f, 0.04f, 1f);
                    scene.Post.LightColor = new Color(0.25f, 0.24f, 0.22f, 1f);
                    scene.Post.AmbientColor = new Color(0.05f, 0.05f, 0.07f, 1f);
                    scene.Post.Bloom.Enabled = true;
                    scene.Post.RenderScale = RenderScale.MatchViewport;   // a halo the grid can see, see the bloom fact
                    scene.Camera.Azimuth = 0.5f;
                    scene.Camera.Elevation = 0.7f;
                    scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(5f, 1.5f, 3f));
                },
                drawFrame: scene =>
                {
                    // The glTF triangle is a unit right triangle in x and y, so it stands as a lit panel.
                    scene.Draw(prop, Matrix4x4.CreateScale(1.4f) * Matrix4x4.CreateTranslation(-2.1f, 0f, 0f));
                    scene.Draw(flat, Matrix4x4.CreateTranslation(-0.2f, 0.4f, 0f), new Color(0.3f, 0.3f, 0.3f, 1f));
                    scene.Draw(reference, Matrix4x4.CreateTranslation(1.0f, 0.4f, 0f), new Color(0.5f, 0.5f, 0.5f, 1f));
                    scene.DrawSkinned(tube, tubeMesh.RestPose, Matrix4x4.CreateTranslation(1.6f, 0.3f, 0.9f),
                        new Color(0.35f, 0.35f, 0.35f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_gltf_emissive", rgba, GW, GH);
        }

        // Eight horizontal stripes, warm and dark, so the golden sees the map's pattern rather than a flat factor.
        static byte[] Stripes()
        {
            const int N = 32;
            var rgba = new byte[N * N * 4];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    byte v = (y / 4 & 1) == 0 ? (byte)255 : (byte)40;
                    int i = (y * N + x) * 4;
                    rgba[i] = v; rgba[i + 1] = v; rgba[i + 2] = v; rgba[i + 3] = 255;
                }
            return KhaozEngine.Imaging.PngWriter.Encode(rgba, N, N);
        }
    }
}
