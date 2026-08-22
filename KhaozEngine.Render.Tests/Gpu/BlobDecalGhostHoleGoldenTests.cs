using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PICTURE THE <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see> FIX CORRECTS.
    /// Everything else about that issue is a timeline: uploads, draw ordinals and byte comparisons. This is the one
    /// row that looks at the render.
    ///
    /// <para><b>THE MECHANISM, END TO END.</b> A frame with a shadow blob AND a queued ground decal runs
    /// <c>GroundDecalRenderer.Draw</c> twice. Before 17.39.0 both passes wrote the same uniform range, so on the
    /// three engine-owned native backends the ring handed the blob pass the MAIN pass's <c>TimeQ.w = 1</c>, which is
    /// the dynamic-geometry reject turned on. The blob pass runs at <c>Scene3D.cs:1903</c> after a DEPTH-ONLY
    /// resolve, so under MSAA the normal target it would sample is the PREVIOUS frame's resolve, and the reject
    /// discards every pixel that frame tagged dynamic (<c>ShaderSources.Decal.cs</c>, the
    /// <c>TimeQ.w &gt; 0.5 &amp;&amp; NormalTex.a &lt; 0.03</c> line). A skinned character therefore punches a
    /// character-shaped GHOST HOLE in this frame's blob, one frame late and in the place the character has already
    /// left.</para>
    ///
    /// <para><b>SO THE CHARACTER IS THERE ON FRAME ONE AND GONE ON FRAME TWO,</b> and the assertion reads the floor
    /// where it stood. That is what makes the fault visible and what no committed golden covers: a golden that
    /// draws a blob draws the same thing every frame, and a stale tag that matches this frame's tag hides in the
    /// character's own silhouette.</para>
    ///
    /// <para><b>MSAA IS LOAD-BEARING.</b> Without it there is no resolve at all: <c>NormalTex</c> IS the model
    /// pass's own attachment, already written THIS frame, so the reject reads current tags and the stale read
    /// cannot happen. (The FIRST frame is not the case to assert either, for the neighbouring reason that an
    /// unresolved target holds whatever the driver left in it, which on Metal reads opaque and rejects nothing.)</para>
    ///
    /// <para>Backend-independent: it asserts that floor inside the blob is DARKER than floor outside it, not
    /// committed pixels. The "Golden" in the name enrols it in the cross-backend GPU matrix, which is where it
    /// earns its keep, because the fault exists only on the native backends and the incumbents render it
    /// correctly. Skipped unless KE_GPU_TESTS=1.</para>
    /// </summary>
    public sealed class BlobDecalGhostHoleGoldenTests
    {
        const int W = 360, H = 360;

        // Where the character stands on frame one: dead centre of the blob, standing on the floor.
        static readonly Matrix4x4 CharacterModel = Matrix4x4.CreateTranslation(0f, 0.02f, 0f);

        // The three points the verdict is read from, all on the floor and all inside the blob except the last.
        //
        // BEHIND is the load-bearing one: the camera ray to it passes through the character's body about a metre
        // up, so on frame one the character HID it and tagged those screen pixels dynamic, and on frame two the
        // character is gone and the pixels are plain floor the blob must cover. Under the collapse they are the
        // ghost hole. Picking floor BEHIND the character rather than under it matters, because a character's own
        // footprint is a small part of its silhouette and the interior of an open mesh shows floor straight
        // through.
        static readonly Vector3 Behind = new(-1.5f, 0.01f, -1.5f);
        // Beside: also inside the blob, never hidden by the character. The blob level to compare against, measured
        // rather than assumed, so the test does not care how dark this backend's blob is.
        static readonly Vector3 Beside = new(1.7f, 0.01f, -1.7f);
        // Outside the blob disc entirely: the bare-floor level, which is what a discarded blob pixel falls back to.
        static readonly Vector3 Outside = new(4f, 0.01f, 4f);

        [GpuFact]
        public void A_departed_character_leaves_no_hole_in_the_next_frames_blob()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            Assert.True(gd.Capabilities.MaxMsaaSampleCount >= 2,
                "this device reports no MSAA, and without a multisampled normal target the blob pass samples the "
                + "model pass's own attachment rather than the previous frame's resolve, which is the whole fault "
                + "being pinned");

            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            SkinnedGltfMesh character = Character();
            SkinnedMeshHandle characterHandle = scene.LoadSkinnedMesh(character);
            scene.Post.TransparentBackground = false;
            scene.Post.Background = BackgroundMode.Solid;
            scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
            scene.Camera.Frame(new Vector3(0f, 1.0f, 0f), new Vector3(6f, 5f, 6f));
            Assert.True(scene.Post.EffectiveMsaaSamples >= 2, "MSAA did not survive the AA selection");

            // FRAME ONE: the character stands in the middle of the blob and tags its pixels dynamic.
            preview.Capture(s =>
            {
                Standing(s, floor);
                s.DrawSkinned(characterHandle, character.RestPose, CharacterModel,
                    new Color(0.2f, 0.8f, 0.35f, 1f));
            });

            // FRAME TWO: the character is gone. The blob must cover the floor it was standing on, and does only if
            // the blob pass is reading its OWN uniform slot with the reject off.
            preview.Capture(s => Standing(s, floor));

            byte[] rgba = preview.ReadbackRgba();
            int behind = SampleGray(rgba, Behind);
            int beside = SampleGray(rgba, Beside);
            int outside = SampleGray(rgba, Outside);

            Assert.True(behind >= 0 && beside >= 0 && outside >= 0, "a sample point projected off-screen");
            // The blob has to be doing something at all, or the verdict below is two readings of bare floor.
            Assert.True(beside < outside - 15,
                $"the blob is not darkening the floor beside the character ({beside} against {outside} outside the "
                + "disc), so this frame cannot say anything about a hole in it");
            Assert.True(behind < beside + 12,
                $"a one-frame ghost hole: the floor the character hid last frame reads {behind} against {beside} "
                + $"for blob-covered floor beside it and {outside} for bare floor outside the disc, so the blob was "
                + "discarded exactly where last frame's dynamic tags are. The blob decal pass and the main decal "
                + "pass must read SEPARATE slots of the decal frame UBO (GroundDecalRenderer.FrameSlots.cs, #483). "
                + "Sharing one range lets the native backends' uniform ring hand the blob pass the main pass's "
                + "dynamic reject, which it then applies to a normal target it has not resolved this frame.");
        }

        // The floor, the blob, and a ground decal parked out of shot. The decal is the other half of the pair:
        // without it the main pass never runs, the frame writes the decal frame UBO once, and there is nothing for
        // the ring to collapse.
        static void Standing(Scene3D s, MeshHandle floor)
        {
            s.Draw(floor, Matrix4x4.Identity);
            s.AddShadowBlob(new ShadowBlob(new Vector3(0f, 0f, 0f), groundY: 0f, radius: 2.5f));
            s.DrawGroundDecal(new GroundDecal
            {
                Shape = DecalShape.Circle, Center = new Vector3(-4.0f, 0f, -4.0f),
                Size = new Vector4(0.5f, 0f, 0f, 0f),
                FillColor = new Color(1f, 0.2f, 0.1f, 0.6f),
                OutlineColor = new Color(1f, 0.9f, 0.2f, 0.9f),
                EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha,
                YTolerance = 0.3f, MaxStep = 0.4f,
            });
        }

        // A skinned column: the character path is what the model pass tags dynamic (a rigid mesh is a legitimate
        // decal receiver and is tagged static). Two metres of it, so its silhouette covers real floor BEHIND it
        // rather than only its own footprint.
        static SkinnedGltfMesh Character() => SkinnedMeshBuilder.BuildTube(0.55f, 2.0f, 8, 12, 4, Axis.Y);

        // Average the grey of a small window around a projected world point.
        static int SampleGray(byte[] rgba, Vector3 world)
        {
            if (!Project(world, out int px, out int py)) return -1;
            long sum = 0; int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    sum += (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3; n++;
                }
            return n == 0 ? -1 : (int)(sum / n);
        }

        // Rebuild the capture camera and project. WorldToScreen returns a top-left-origin, y-down pixel, matching
        // the GpuReadback row-major/top-left buffer, so the pixel indexes the readback directly.
        static bool Project(Vector3 world, out int px, out int py)
        {
            var cam = new IsoCamera3D();
            cam.Frame(new Vector3(0f, 1.0f, 0f), new Vector3(6f, 5f, 6f));
            cam.AspectRatio = (float)W / H;   // Scene3D sets this from the viewport at render time (after Frame).
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) { px = py = -1; return false; }
            px = (int)(p.X + 0.5f); py = (int)(p.Y + 0.5f);
            return true;
        }
    }
}
