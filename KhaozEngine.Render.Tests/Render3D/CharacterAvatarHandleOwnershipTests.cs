using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// WHO OWNS THE SKINNED MESH A GLTF CHARACTER LOAD UPLOADED
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/95">#95</see>). The upload happens partway
    /// through <see cref="CharacterAvatar.TryLoadGltf"/>, and several steps after it can fail: the clip source
    /// parses the asset a second time, the height scan walks the vertices, and both constructors validate their
    /// arguments. Only one of those exits used to release the upload, so every other failure left a skinned mesh
    /// alive with nothing pointing at it, once per attempt on a retry or editor re-import loop.
    ///
    /// <para>The claim asserted here is the ownership rule itself, in both directions: a load that returns null
    /// released the upload, and a load that returns an avatar did NOT (the avatar owns it, and the game frees it
    /// through <c>CharacterAvatar.Mesh</c>). A test that only checked the failure half would pass just as happily
    /// against a method that unloaded on every path and handed back a dead handle.</para>
    ///
    /// <para>Device-free: a real <see cref="Scene3D"/> over <see cref="FakeGpuDevice"/>, whose factory records
    /// every buffer it handed out and whether it was freed. The unload retires rather than destroys, so the
    /// buffers are read after pumping the retire queue's frame delay, exactly as
    /// <see cref="Scene3DUnloadRetireTests"/> does.</para>
    ///
    /// <para>The composition step is driven through <c>CharacterAvatar.Compose</c> rather than the public
    /// <see cref="CharacterAvatar.TryLoadGltf"/>, because the failures this covers cannot be arranged from a file:
    /// every step between the upload and the return succeeds on any asset the loader read in the first place.
    /// <see cref="TryLoadGltf_OnAMissingAsset_ReportsAndUploadsNothing"/> holds the public entry point down.</para>
    /// </summary>
    // CharacterAvatar is Obsolete (superseded by ReplicatedCharacterAnimators), and is exercised on purpose here:
    // it is still public API and a leak in it is still a leak. Same shape as CharacterAvatarTests.
#pragma warning disable CS0618
    public sealed class CharacterAvatarHandleOwnershipTests
    {
        static Scene3D NewScene(out FakeGpuResourceFactory factory)
        {
            var device = new FakeGpuDevice();
            factory = (FakeGpuResourceFactory)device.Factory;
            IGpuTexture colour = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer target = factory.CreateFramebuffer(null, colour);
            return new Scene3D(device, target.Outputs);
        }

        // A one-bone rig WITH a skeleton, which is what the compose step requires. SkinnedMeshBuilder's tubes carry
        // no skeleton (they are code-driven rigs), so the geometry is reused and a one-joint skeleton attached.
        static SkinnedGltfMesh RiggedTube()
        {
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 4, 6, 1, Axis.Z);
            var skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
            return new SkinnedGltfMesh(tube.Vertices, tube.Indices32, tube.InverseBind, tube.RestPose, skeleton);
        }

        static CharacterController3D Controller() => new() { CapsuleHalfHeight = 0.9f };

        static AnimationClip Park(string name)
        {
            var joint = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f },
                    new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { joint });
        }

        // The buffers one upload created (a vertex buffer and an index buffer). Named by the range the factory grew
        // by, the way Scene3DUnloadRetireTests names them, since the scene creates plenty of its own.
        static List<FakeBuffer> UploadedSince(FakeGpuResourceFactory factory, int from)
        {
            List<FakeBuffer> buffers = factory.Buffers.GetRange(from, factory.Buffers.Count - from);
            Assert.Equal(2, buffers.Count);
            return buffers;
        }

        // Run the retire queue past its frame delay so anything the unload retired has actually been destroyed.
        static void PumpRetirement(Scene3D scene)
        {
            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) scene.Begin();
        }

        /// <summary>
        /// THE LEAK #95 NAMED. The clip source throws (a parse error in the asset's animations, the issue's first
        /// example), which lands in the catch that had no handle to release. The upload must be gone.
        /// </summary>
        [Fact]
        public void Compose_WhenTheClipSourceThrows_ReleasesTheUpload()
        {
            Scene3D scene = NewScene(out FakeGpuResourceFactory factory);
            int from = factory.Buffers.Count;
            string? failure = null;

            CharacterAvatar? avatar = CharacterAvatar.Compose(scene, RiggedTube(), default,
                () => throw new InvalidOperationException("animation parse failed"),
                Controller(), null, 0.15f, 0f, m => failure = m);

            Assert.Null(avatar);
            Assert.Equal("animation parse failed", failure);
            List<FakeBuffer> uploaded = UploadedSince(factory, from);
            PumpRetirement(scene);
            foreach (FakeBuffer b in uploaded)
                Assert.True(b.Disposed, "a failed character load left its skinned mesh upload alive");
        }

        /// <summary>
        /// The one failure path that DID release before #95, kept pinned across the rewrite: a rig whose clips map
        /// to none of the locomotion states cannot drive an avatar, and its upload goes back too.
        /// </summary>
        [Fact]
        public void Compose_WhenTheRigHasNoLocomotionClips_ReleasesTheUpload()
        {
            Scene3D scene = NewScene(out FakeGpuResourceFactory factory);
            int from = factory.Buffers.Count;
            string? failure = null;

            CharacterAvatar? avatar = CharacterAvatar.Compose(scene, RiggedTube(), default,
                () => new[] { Park("Sprint"), Park("Crouch") },
                Controller(), null, 0.15f, 0f, m => failure = m);

            Assert.Null(avatar);
            Assert.Contains("locomotion clips", failure!);
            List<FakeBuffer> uploaded = UploadedSince(factory, from);
            PumpRetirement(scene);
            foreach (FakeBuffer b in uploaded) Assert.True(b.Disposed);
        }

        /// <summary>
        /// THE OTHER HALF OF THE RULE. A successful load HANDS the handle to the avatar, so nothing may be released
        /// on the way out. Without this row, unloading unconditionally would satisfy the two above and ship a
        /// character that draws through a freed mesh.
        /// </summary>
        [Fact]
        public void Compose_WhenItReturnsAnAvatar_KeepsTheUpload()
        {
            Scene3D scene = NewScene(out FakeGpuResourceFactory factory);
            int from = factory.Buffers.Count;

            CharacterAvatar? avatar = CharacterAvatar.Compose(scene, RiggedTube(), default,
                () => new[] { Park("Idle"), Park("Walk"), Park("Run") },
                Controller(), null, 0.15f, 0f, null);

            Assert.NotNull(avatar);
            List<FakeBuffer> uploaded = UploadedSince(factory, from);
            PumpRetirement(scene);
            foreach (FakeBuffer b in uploaded)
                Assert.False(b.Disposed, "the avatar owns its skinned mesh; the load must not have released it");

            // And the handle it owns is still live: unloading it is the game's job, and it works.
            scene.UnloadSkinnedMesh(avatar!.Mesh);
            PumpRetirement(scene);
            foreach (FakeBuffer b in uploaded) Assert.True(b.Disposed);
        }

        /// <summary>
        /// The public entry point over an asset that is not there: null, the reason reported, no throw, and nothing
        /// uploaded to leak in the first place (the read fails before the upload).
        /// </summary>
        [Fact]
        public void TryLoadGltf_OnAMissingAsset_ReportsAndUploadsNothing()
        {
            Scene3D scene = NewScene(out FakeGpuResourceFactory factory);
            int from = factory.Buffers.Count;
            string? failure = null;

            CharacterAvatar? avatar = CharacterAvatar.TryLoadGltf(scene,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ke_absent_{Guid.NewGuid():N}.glb"),
                Controller(), onFailure: m => failure = m);

            Assert.Null(avatar);
            Assert.False(string.IsNullOrEmpty(failure));
            Assert.Equal(from, factory.Buffers.Count);
        }
    }
#pragma warning restore CS0618
}
