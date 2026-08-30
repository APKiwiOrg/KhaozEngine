using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The keyed idempotent loads of <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/250">#250</see>:
    /// a long-lived scene reloading the same asset set every run restart used to re-upload it every time, so each
    /// consumer hand-rolled a static handle field keyed by asset path. Device-free (the fake device is enough to
    /// prove the dedup, since the whole point is that the second call never reaches the GPU at all).
    /// </summary>
    public sealed class Scene3DSharedAssetTests
    {
        const int W = 64, H = 48;

        static IGpuFramebuffer NewTarget(IGpuResourceFactory f)
        {
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            return f.CreateFramebuffer(null, tex);
        }

        static GltfMesh Triangle()
        {
            var verts = new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One),
            };
            return new GltfMesh(verts, new uint[] { 0, 1, 2 });
        }

        [Fact]
        public void GetOrLoadTexture_LoadsOnceAndReturnsTheSameHandle()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            int loads = 0;
            Scene3D.TextureHandle Load()
            {
                loads++;
                return scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None);
            }

            Scene3D.TextureHandle first = scene.GetOrLoadTexture("art/hull.png", Load);
            Scene3D.TextureHandle second = scene.GetOrLoadTexture("art/hull.png", Load);

            Assert.Equal(1, loads);
            Assert.Equal(first.ListIndex, second.ListIndex);
            Assert.True(first.IsValid);
        }

        [Fact]
        public void GetOrLoadTexture_DifferentKeysAreDifferentUploads()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            Scene3D.TextureHandle a = scene.GetOrLoadTexture(
                "a", () => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None));
            Scene3D.TextureHandle b = scene.GetOrLoadTexture(
                "b", () => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None));

            Assert.NotEqual(a.ListIndex, b.ListIndex);
        }

        [Fact]
        public void GetOrLoadMesh_LoadsOnceAndReturnsTheSameHandle()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            int loads = 0;
            MeshHandle Load() { loads++; return scene.LoadMesh(Triangle()); }

            MeshHandle first = scene.GetOrLoadMesh("ship.glb", Load);
            MeshHandle second = scene.GetOrLoadMesh("ship.glb", Load);

            Assert.Equal(1, loads);
            Assert.Equal(first, second);
        }

        [Fact]
        public void UnloadSharedMesh_DropsTheEntrySoTheNextGetReloads()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            int loads = 0;
            MeshHandle Load() { loads++; return scene.LoadMesh(Triangle()); }

            MeshHandle first = scene.GetOrLoadMesh("ship.glb", Load);
            Assert.True(scene.UnloadSharedMesh("ship.glb"));
            MeshHandle second = scene.GetOrLoadMesh("ship.glb", Load);

            Assert.Equal(2, loads);
            // The slot is recycled, so the reload lands on the same index with a bumped generation. Either way it
            // is a genuinely fresh upload, which is what the reload contract promises.
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void UnloadShared_OnAnUnknownKeyIsFalseAndHarmless()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            Assert.False(scene.UnloadSharedMesh("never-loaded"));
            Assert.False(scene.UnloadSharedTexture("never-loaded"));
            Assert.False(scene.UnloadSharedSkinnedMesh("never-loaded"));
        }

        [Fact]
        public void SharedAssetCount_TracksWhatIsCachedAcrossAllThreeFamilies()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            Assert.Equal(0, scene.SharedAssetCount);
            scene.GetOrLoadMesh("m", () => scene.LoadMesh(Triangle()));
            scene.GetOrLoadTexture("t", () => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None));
            Assert.Equal(2, scene.SharedAssetCount);

            // Same keys in different families do not collide: each family has its own key space.
            scene.GetOrLoadTexture("m", () => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None));
            Assert.Equal(3, scene.SharedAssetCount);

            scene.UnloadSharedMesh("m");
            Assert.Equal(2, scene.SharedAssetCount);
        }

        [Fact]
        public void GetOrLoad_ThrowingLoaderCachesNothingSoTheNextCallRetries()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            int attempts = 0;
            MeshHandle Flaky()
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("asset file missing");
                return scene.LoadMesh(Triangle());
            }

            Assert.Throws<InvalidOperationException>(() => scene.GetOrLoadMesh("ship.glb", Flaky));
            Assert.Equal(0, scene.SharedAssetCount);

            MeshHandle recovered = scene.GetOrLoadMesh("ship.glb", Flaky);
            Assert.Equal(2, attempts);
            Assert.Equal(1, scene.SharedAssetCount);
            Assert.NotEqual(default, recovered);
        }

        [Fact]
        public void GetOrLoad_KeysAreOrdinalAndCaseSensitive()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            scene.GetOrLoadMesh("Ship.glb", () => scene.LoadMesh(Triangle()));
            scene.GetOrLoadMesh("ship.glb", () => scene.LoadMesh(Triangle()));
            Assert.Equal(2, scene.SharedAssetCount);
        }

        [Fact]
        public void GetOrLoad_NullKeyOrLoaderThrows()
        {
            using var device = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            Assert.Throws<ArgumentNullException>(() => scene.GetOrLoadMesh(null!, () => scene.LoadMesh(Triangle())));
            Assert.Throws<ArgumentNullException>(() => scene.GetOrLoadMesh("k", null!));
        }
    }
}
