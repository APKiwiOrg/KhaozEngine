using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE PROMISE THE RECEIVER HALF MAKES: a scene that asks for no point-light shadow renders exactly as it
    /// did before point-light shadows existed. Every receiver family now declares the atlas, carries the frame
    /// block's 272-byte point tail and calls <c>samplePointShadow</c>, so the compiled shaders all moved, and this
    /// is what says the PIXELS did not.
    /// <para>
    /// The gate being asserted is one branch: <c>pointLight.ShadowParams.x >= 0.0</c>. Every light without a slot
    /// reads -1 there, so the sample, the texture read and the multiply are all skipped and the accumulation is
    /// the pre-shadow arithmetic. Three renders of the same two-point-light scene are compared BYTE for byte
    /// rather than within a tolerance, because a skipped branch is not an approximation.
    /// </para>
    /// <para>
    /// It is a relative test with no committed reference image, so it runs on every backend the matrix has and
    /// needs no per-backend bake. Skipped with no device.
    /// </para>
    /// </summary>
    public sealed class PointShadowByteIdentityGpuTests
    {
        const int W = 128, H = 128;

        // Dim the globals so the point lights dominate the lit term. A frame whose colour came from the key light
        // would be byte-identical whatever the point loop did, which would make this test pass for no reason.
        static void DarkenGlobals(Scene3D scene)
        {
            scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        }

        static long CentreBrightness(byte[] rgba)
        {
            long sum = 0;
            for (int y = H / 2 - 8; y < H / 2 + 8; y++)
                for (int x = W / 2 - 8; x < W / 2 + 8; x++)
                {
                    int i = (y * W + x) * 4;
                    sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
                }
            return sum;
        }

        /// <summary>One capture of the same two-point-light box. <paramref name="configure"/> runs on the scene
        /// before the frame, and <paramref name="shadow"/> is what both lights ask for.</summary>
        static byte[] Capture(LightShadow shadow, Action<Scene3D>? configure = null, bool useFiveArgOverload = true)
        {
            MeshHandle box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    DarkenGlobals(scene);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
                    configure?.Invoke(scene);
                },
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity);
                    Add(scene, new Vector3(2f, 2f, 2f), new Color(1f, 0.8f, 0.6f, 1f));
                    Add(scene, new Vector3(-2f, 1f, 2.5f), new Color(0.4f, 0.6f, 1f, 1f));
                }, frames: 1);

            void Add(Scene3D scene, Vector3 at, Color colour)
            {
                if (useFiveArgOverload) scene.AddLight(at, colour, 8f, 4f, shadow);
                else scene.AddLight(at, colour, 8f, 4f);
            }
        }

        [GpuFact]
        public void TheTwoLightSceneIsActuallyLitByItsPointLights()
        {
            // The control the three identity assertions below lean on. If the box were black in every capture they
            // would all pass and say nothing, so pin that the point lights reach the surface first.
            byte[] lit = Capture(LightShadow.None, useFiveArgOverload: false);
            byte[] unlit = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    DarkenGlobals(scene);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
                },
                drawFrame: _ => { }, frames: 1);

            Assert.True(CentreBrightness(lit) > CentreBrightness(unlit) * 3,
                "the scene these identity assertions compare is not lit by its point lights, so comparing it "
                + "proves nothing about the point loop.");
        }

        [GpuFact]
        public void TheFiveArgumentOverloadWithNoneIsTheFourArgumentPathByteForByte()
        {
            // LightShadow.None is default(LightShadow), so the four-argument overload forwards exactly this. The
            // two calls must therefore produce one image, which is the API half of the promise.
            byte[] four = Capture(LightShadow.None, useFiveArgOverload: false);
            byte[] five = Capture(LightShadow.None);

            Assert.Equal(four, five);
        }

        [GpuFact]
        public void FlippingThePointShadowSettingsChangesNothingWhileNoLightAsksForOne()
        {
            // The settings knob is not a render path on its own: a scene that queues no request is unshadowed with
            // the feature enabled and unshadowed with it disabled, and the two captures are the same bytes.
            byte[] enabled = Capture(LightShadow.None,
                scene => scene.Post.Quality.Shadows.PointShadows.Enabled = true, useFiveArgOverload: false);
            byte[] disabled = Capture(LightShadow.None,
                scene => scene.Post.Quality.Shadows.PointShadows.Enabled = false, useFiveArgOverload: false);

            Assert.Equal(enabled, disabled);
        }

        [GpuFact]
        public void AHigherBudgetProfileChangesNothingWhileNoLightAsksForOne()
        {
            // A bigger budget than any profile ships is a different atlas shape and therefore different
            // PointShadowAtlas uniforms once a light asks. With no request queued nothing allocates and nothing
            // samples, so the picture is the Default profile's picture.
            byte[] standard = Capture(LightShadow.None, useFiveArgOverload: false);
            byte[] high = Capture(LightShadow.None, scene =>
            {
                scene.Post.Quality.Shadows.PointShadows.FaceResolution = 512;
                scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 16;
            }, useFiveArgOverload: false);

            Assert.Equal(standard, high);
        }
    }
}
