using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// The three showcase rooms that show the rigged character (3D, dungeon, net) used to carry their own copy of
    /// the load, and the copies had already drifted twice
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/189">#189</see>): RoomNet's <c>ModelHeight</c>
    /// lost the degenerate-mesh guard, so a vertexless rig gave it negative infinity where the other two gave 0,
    /// and its empty-clip bail dropped the unload the other two kept. These pin the one shared loader the rooms
    /// call now, through the delegate seam its <see cref="Scene3D"/> overload wraps, so the whole load runs with
    /// no GPU device at all.
    /// </summary>
    public sealed class CharacterRigLoaderTests
    {
        const float CapsuleHalfHeight = 0.9f;

        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        static SkinnedVertex At(float y) => new SkinnedVertex { Position = new Vector3(0f, y, 0f) };

        /// <summary>A rig whose rest mesh spans <paramref name="height"/> model units in Y.</summary>
        static SkinnedGltfMesh Rig(float height, bool skeleton = true) => new SkinnedGltfMesh(
            new[] { At(0f), At(height), At(height * 0.5f) },
            new ushort[] { 0, 1, 2 },
            new[] { Matrix4x4.Identity },
            new[] { Matrix4x4.Identity },
            skeleton ? OneBone() : null);

        /// <summary>A rig with a skeleton and no vertices at all, the stripped-export case RoomNet's copy read as
        /// negative infinity.</summary>
        static SkinnedGltfMesh Vertexless() => new SkinnedGltfMesh(
            Array.Empty<SkinnedVertex>(),
            Array.Empty<ushort>(),
            new[] { Matrix4x4.Identity },
            new[] { Matrix4x4.Identity },
            OneBone());

        static AnimationClip Park(string name)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { jt });
        }

        /// <summary>Clip names the loader maps, so a load through this set reaches the animator build.</summary>
        static IReadOnlyList<AnimationClip> LocomotionClips() => new[]
        {
            Park("Idle"), Park("Walk"), Park("Run"), Park("Jump"), Park("Fall"),
        };

        /// <summary>Counts what the loader asked the scene to do, standing in for the GPU upload and free.</summary>
        sealed class SceneSpy
        {
            public int Uploads;
            public int Unloads;

            public SkinnedMeshHandle Upload(SkinnedGltfMesh mesh, GltfMaterialMaps maps)
            {
                Uploads++;
                return new SkinnedMeshHandle(7, 3);
            }

            public void Unload(SkinnedMeshHandle handle) => Unloads++;
        }

        [Fact]
        public void A_vertexless_rig_measures_zero_rather_than_negative_infinity()
        {
            float height = CharacterRigLoader.ModelHeight(Vertexless());

            Assert.False(float.IsNegativeInfinity(height));
            Assert.Equal(0f, height);
        }

        [Fact]
        public void Model_height_is_the_y_span_of_the_rest_mesh()
        {
            Assert.Equal(2.5f, CharacterRigLoader.ModelHeight(Rig(2.5f)), 4);
        }

        [Fact]
        public void A_loaded_rig_is_scaled_to_the_capsule_height()
        {
            var spy = new SceneSpy();

            CharacterRigLoad load = CharacterRigLoader.Load(
                () => (Rig(2f), default), LocomotionClips, spy.Upload, spy.Unload, CapsuleHalfHeight);

            Assert.True(load.Loaded);
            Assert.NotNull(load.Animators);
            Assert.Equal((CapsuleHalfHeight * 2f) / 2f, load.Scale, 4);
            Assert.Equal(7, load.Mesh.Index);
            Assert.Equal(1, spy.Uploads);
            Assert.Equal(0, spy.Unloads);
        }

        /// <summary>A rig too small to measure keeps unit scale rather than dividing by something near zero.</summary>
        [Fact]
        public void A_degenerate_rig_keeps_unit_scale()
        {
            var spy = new SceneSpy();

            CharacterRigLoad load = CharacterRigLoader.Load(
                () => (Vertexless(), default), LocomotionClips, spy.Upload, spy.Unload, CapsuleHalfHeight);

            Assert.True(load.Loaded);
            Assert.Equal(1f, load.Scale, 4);
        }

        [Fact]
        public void A_rig_with_no_skeleton_does_not_load()
        {
            var spy = new SceneSpy();

            CharacterRigLoad load = CharacterRigLoader.Load(
                () => (Rig(2f, skeleton: false), default), LocomotionClips, spy.Upload, spy.Unload, CapsuleHalfHeight);

            Assert.False(load.Loaded);
            Assert.Null(load.Animators);
            Assert.Equal(0, spy.Uploads);
            Assert.Contains("skeleton", load.Message, StringComparison.Ordinal);
        }

        /// <summary>RoomNet's copy returned here without freeing the mesh it had just uploaded. The shared loader
        /// keeps the other two rooms' unload.</summary>
        [Fact]
        public void A_rig_whose_clips_match_no_locomotion_state_frees_the_mesh()
        {
            var spy = new SceneSpy();
            IReadOnlyList<AnimationClip> unrelated = new[] { Park("Dance"), Park("Wave") };

            CharacterRigLoad load = CharacterRigLoader.Load(
                () => (Rig(2f), default), () => unrelated, spy.Upload, spy.Unload, CapsuleHalfHeight);

            Assert.False(load.Loaded);
            Assert.Null(load.Animators);
            Assert.Equal(spy.Uploads, spy.Unloads);
        }

        [Fact]
        public void A_throwing_load_falls_back_instead_of_propagating()
        {
            var spy = new SceneSpy();

            CharacterRigLoad load = CharacterRigLoader.Load(
                () => throw new InvalidOperationException("bad glb"), LocomotionClips, spy.Upload, spy.Unload, CapsuleHalfHeight);

            Assert.False(load.Loaded);
            Assert.Contains("bad glb", load.Message, StringComparison.Ordinal);
        }
    }
}
