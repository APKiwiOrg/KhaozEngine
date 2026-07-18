using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the dynamic-geometry decal reject (issue #235): a ground decal must conform to the static world
    /// but NOT paint onto skinned characters standing in its Y-band. The model pass tags skinned geometry with
    /// normal-target alpha 0 (static world keeps 1); the main decal pass rejects the tagged pixels.
    /// <para>
    /// Each test renders a scene twice - without the decal, then with it - and compares. The decal must LEAVE the
    /// skinned mesh's pixels unchanged (rejected) while CHANGING the surrounding ground (painted). A rigid control
    /// mesh proves the tag is skinned-specific: a rigid box in the same band is still painted (so the no-skinned
    /// world stays byte-identical). Gated on KE_GPU_TESTS.
    /// </para>
    /// </summary>
    public sealed class GroundDecalDynamicRejectGpuTests
    {
        const int W = 256, H = 256;

        // A big, opaque magenta ground disc centred on the origin with a tall Y-band (like Ruinborne's hazard band)
        // so an object standing on the ground has its whole in-band portion inside the decal footprint.
        static void QueueBandDecal(Scene3D s) => s.DrawGroundDecal(new GroundDecal
        {
            Shape = DecalShape.Circle,
            Center = new Vector3(0f, 0f, 0f),
            Size = new Vector4(6f, 0, 0, 0),
            FillColor = new Color(0.95f, 0.08f, 0.9f, 0.95f),
            OutlineColor = new Color(1f, 0.6f, 1f, 0.95f),
            EdgeThickness = 0.05f, FillFraction = 1f, Blend = DecalBlend.Alpha,
            YTolerance = 0.3f, MaxStep = 1.6f,   // band [-0.3, 1.6] covers ground (0) and the tube top (~1.0)
        });

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static void FrameLookingDown(Scene3D s) =>
            s.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(3.5f, 4.5f, 3.5f));

        // Per-pixel classification of the DECAL-FREE capture: tube pixels are green-dominant, ground pixels greyish.
        static bool IsGreenTube(byte[] px, int i) =>
            px[i + 3] > 200 && px[i + 1] > 90 && px[i + 1] - px[i] > 35 && px[i + 1] - px[i + 2] > 35;
        static bool IsGreyGround(byte[] px, int i) =>
            px[i + 3] > 200 && Math.Abs(px[i] - px[i + 1]) < 30 && Math.Abs(px[i + 1] - px[i + 2]) < 30
            && px[i] > 60 && px[i] < 210 && !IsGreenTube(px, i);
        static int ChannelDiff(byte[] a, byte[] b, int i) =>
            Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);

        static void AssertRejectedOnSkinned(IGpuDevice gd, Render3DPreview preview, byte[] noDecal, byte[] withDecal, string tag)
        {
            int tube = 0, tubeTouched = 0, ground = 0, groundPainted = 0;
            for (int i = 0; i + 3 < noDecal.Length; i += 4)
            {
                if (IsGreenTube(noDecal, i))
                {
                    tube++;
                    if (ChannelDiff(noDecal, withDecal, i) > 45) tubeTouched++;
                }
                else if (IsGreyGround(noDecal, i))
                {
                    ground++;
                    if (ChannelDiff(noDecal, withDecal, i) > 45) groundPainted++;
                }
            }
            Assert.True(tube > 300, $"[{tag}] skinned tube should cover a meaningful area, got {tube} px");
            Assert.True(ground > 300, $"[{tag}] bare ground should be visible around the tube, got {ground} px");
            // The decal must be ACTIVE where the tube stands: most of the surrounding ground is repainted.
            Assert.True(groundPainted > ground / 2, $"[{tag}] decal should paint the ground: {groundPainted}/{ground}");
            // ...and it must LEAVE THE SKINNED TUBE ALONE: at most a sliver of silhouette pixels may shift.
            Assert.True(tubeTouched < tube / 20, $"[{tag}] decal must not paint the skinned tube: {tubeTouched}/{tube} touched");

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            PngWriter.Save(Path.Combine(dir, $"decal_dynreject_{tag}.png"), withDecal, W, H);
        }

        [GpuFact]
        public void CpuSkinned_tube_is_not_painted_by_ground_decal_while_ground_is()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Hdr.Enabled = false;   // LDR: keep the magenta separable from the green tube
            FrameLookingDown(preview.Scene);

            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 3f, 12, 12, 6, Axis.X);
            SkinnedMeshHandle skinned = preview.Scene.LoadSkinnedMesh(tube);
            Matrix4x4 tubeAt = Matrix4x4.CreateTranslation(0f, 0.5f, 0f);   // rest on the floor, top ~ y=1.0
            var green = new Color(0.13f, 0.78f, 0.2f, 1f);
            var grey = new Color(0.5f, 0.5f, 0.5f, 1f);

            void Base(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity, grey);
                s.DrawSkinned(skinned, tube.RestPose, tubeAt, green);
            }

            byte[] noDecal = Read(gd, preview.Capture(Base));
            byte[] withDecal = Read(gd, preview.Capture(s => { Base(s); QueueBandDecal(s); }));

            AssertRejectedOnSkinned(gd, preview, noDecal, withDecal, "cpu");
            preview.Scene.UnloadSkinnedMesh(skinned);
        }

        [GpuFact]
        public void GpuSkinned_tube_is_not_painted_by_ground_decal()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Hdr.Enabled = false;
            preview.Scene.UseGpuSkinning = true;   // exercise the dedicated GPU-skinning shader path
            FrameLookingDown(preview.Scene);

            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 3f, 12, 12, 6, Axis.X);
            SkinnedMeshHandle skinned = preview.Scene.LoadSkinnedMesh(tube);
            Matrix4x4 tubeAt = Matrix4x4.CreateTranslation(0f, 0.5f, 0f);
            var green = new Color(0.13f, 0.78f, 0.2f, 1f);
            var grey = new Color(0.5f, 0.5f, 0.5f, 1f);

            void Base(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity, grey);
                s.DrawSkinned(skinned, tube.RestPose, tubeAt, green);
            }

            byte[] noDecal = Read(gd, preview.Capture(Base));
            byte[] withDecal = Read(gd, preview.Capture(s => { Base(s); QueueBandDecal(s); }));

            AssertRejectedOnSkinned(gd, preview, noDecal, withDecal, "gpu");
            preview.Scene.UnloadSkinnedMesh(skinned);
        }

        [GpuFact]
        public void Rigid_mesh_in_the_band_is_still_painted_no_skinned_world_unchanged()
        {
            // The tag is skinned-specific: a RIGID mesh in the same band must still receive the decal, so a scene
            // with no skinned geometry is byte-identical to before the reject existed. Blue rigid box, magenta decal.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Hdr.Enabled = false;
            FrameLookingDown(preview.Scene);

            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.3f));
            Matrix4x4 boxAt = Matrix4x4.CreateTranslation(0f, 0.4f, 0f);   // top ~ y=1.05, inside the band
            var blue = new Color(0.15f, 0.2f, 0.85f, 1f);
            var grey = new Color(0.5f, 0.5f, 0.5f, 1f);

            void Base(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity, grey);
                s.Draw(box, boxAt, blue);
            }

            byte[] noDecal = Read(gd, preview.Capture(Base));
            byte[] withDecal = Read(gd, preview.Capture(s => { Base(s); QueueBandDecal(s); }));

            // The blue box top is in the band, so the decal SHOULD paint it (rigid = static world). Count blue-box
            // pixels whose colour shifts toward magenta once the decal is added.
            int boxTop = 0, boxPainted = 0;
            for (int i = 0; i + 3 < noDecal.Length; i += 4)
            {
                bool isBlue = noDecal[i + 3] > 200 && noDecal[i + 2] > 90
                    && noDecal[i + 2] - noDecal[i] > 30 && noDecal[i + 2] - noDecal[i + 1] > 30;
                if (!isBlue) continue;
                boxTop++;
                if (ChannelDiff(noDecal, withDecal, i) > 45) boxPainted++;
            }
            Assert.True(boxTop > 200, $"blue rigid box should be visible, got {boxTop} px");
            Assert.True(boxPainted > boxTop / 2, $"rigid box must still receive the decal: {boxPainted}/{boxTop}");
        }
    }
}
