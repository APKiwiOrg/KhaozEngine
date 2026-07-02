using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU image regression for the collision-shape debug overlay: builds a <see cref="CollisionShapeOverlay"/>
    /// from one static per shape kind (box, sphere, capsule, cylinder, convex hull, compound), draws it through
    /// <see cref="Scene3D.DrawOverlayMesh(MeshHandle,System.Numerics.Matrix4x4)"/>, and compares the framebuffer
    /// to a committed reference grid. Exercises every <see cref="CollisionShapeMesh.Build"/> converter in one
    /// frame so a winding/pose/color regression in any of them moves the golden. Skipped unless KE_GPU_TESTS=1
    /// (needs a Metal device).
    /// </summary>
    public sealed class CollisionOverlayGoldenTests
    {
        const int W = 480, H = 320;

        [GpuFact]
        public void Golden3D_CollisionOverlay_AllShapeKinds()
        {
            // One static per shape kind, spread out along X/Z so none overlap in the framed view.
            var statics = new[]
            {
                new CollisionStatic(new BoxShape(new Vector3(0.5f, 0.4f, 0.6f)),
                    new Pose(new Vector3(-3.0f, 0.4f, -1.5f), Quaternion.Identity)),
                new CollisionStatic(new SphereShape(0.6f),
                    new Pose(new Vector3(-1.4f, 0.6f, 1.6f), Quaternion.Identity)),
                new CollisionStatic(new CapsuleShape(0.35f, 0.8f),
                    new Pose(new Vector3(0.4f, 0.75f, -1.8f), Quaternion.Identity)),
                new CollisionStatic(new CylinderShape(0.4f, 1.0f),
                    new Pose(new Vector3(2.0f, 0.0f, 1.4f), Quaternion.Identity)),
                // Convex hull: the 8 corners of a small cube, offset so its centroid sits above its pose origin.
                new CollisionStatic(
                    new ConvexHullShape(new[]
                    {
                        new Vector3(-0.4f, 0.0f, -0.4f), new Vector3(0.4f, 0.0f, -0.4f),
                        new Vector3(0.4f, 0.8f, -0.4f), new Vector3(-0.4f, 0.8f, -0.4f),
                        new Vector3(-0.4f, 0.0f, 0.4f), new Vector3(0.4f, 0.0f, 0.4f),
                        new Vector3(0.4f, 0.8f, 0.4f), new Vector3(-0.4f, 0.8f, 0.4f),
                    }),
                    new Pose(new Vector3(3.6f, 0.0f, -1.2f), Quaternion.Identity)),
                // Compound: two boxes at distinct local poses under one static (child kinds count individually).
                new CollisionStatic(
                    new CompoundShape(new[]
                    {
                        new CompoundChild(new BoxShape(new Vector3(0.35f, 0.35f, 0.35f)), Pose.At(new Vector3(-0.5f, 0.35f, 0f))),
                        new CompoundChild(new BoxShape(new Vector3(0.35f, 0.35f, 0.35f)), Pose.At(new Vector3(0.5f, 0.35f, 0f))),
                    }),
                    new Pose(new Vector3(-0.2f, 0.0f, 3.4f), Quaternion.Identity)),
            };

            var overlay = new CollisionShapeOverlay();

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
                    // Fixed top-down-ish framing wide enough to hold all six proxies without overlap.
                    scene.Camera.Frame(Vector3.Zero, new Vector3(7.5f, 8.5f, 7.5f));

                    overlay.Build(scene, statics);
                    overlay.Enabled = true;
                },
                drawFrame: scene =>
                {
                    overlay.Draw(scene);
                },
                frames: 2);

            overlay.Dispose();

            GoldenCompare.AssertOrUpdate("collision_overlay", rgba, W, H);
        }
    }
}
