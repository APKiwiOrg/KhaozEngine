using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The follow camera's opt-in occlusion spring-arm (<see cref="FollowCamera3D.Occlusion"/>): a sweep against a
    /// real <see cref="BepuPhysicsWorld"/> pulls the eye in along the boom when a wall sits between the target and
    /// the geometric eye, so a roofed room or a tight corridor never lets the camera clip through geometry. Mirrors
    /// the existing GroundHeight opt-in test style (see <see cref="FollowCamera3DTests"/>): the default null leaves
    /// the eye byte-identical to the pre-occlusion behaviour.
    /// </summary>
    public class FollowCamera3DOcclusionTests
    {
        [Fact]
        public void Occlusion_is_null_by_default_and_the_eye_is_unchanged()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.Null(cam.Occlusion);
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void No_occluder_in_the_world_leaves_the_eye_at_full_distance()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();   // empty world: nothing along the boom to hit
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, Occlusion = world,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void A_wall_between_target_and_eye_pulls_the_eye_in_without_penetrating_it()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            // A wall spanning the boom path (yaw 0, pitch 0: the boom runs straight down +Z), its near face at
            // z = 4.8, between the target at the origin and the geometric eye at z = 10.
            Vector3 wallHalfExtents = new(2f, 2f, 0.2f);
            Vector3 wallCentre = new(0f, 0f, 5f);
            world.AddStatic(new BoxShape(wallHalfExtents), Pose.At(wallCentre));
            world.Step(1f / 60f);

            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, Occlusion = world,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;

            Vector3 eye = cam.Eye;
            float distFromTarget = Vector3.Distance(eye, cam.Target);
            Assert.True(distFromTarget < cam.Distance - 1e-3f, $"eye should be pulled in, dist={distFromTarget}");

            float wallNearFaceZ = wallCentre.Z - wallHalfExtents.Z;
            Assert.True(eye.Z < wallNearFaceZ, $"eye should stay on the target side of the wall, eye={eye}");
        }
    }
}
