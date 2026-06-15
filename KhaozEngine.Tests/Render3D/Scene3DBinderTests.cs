using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The ECS->Scene3D binder: draws every entity with both <see cref="Transform3D"/> and
    /// <see cref="MeshInstance"/>. Tested headlessly with a real <see cref="World"/> + a recording delegate
    /// (no GPU) — the binder's selection + transform/tint resolution is pure.
    /// </summary>
    public class Scene3DBinderTests
    {
        [Fact]
        public void Submit_DrawsOnlyEntitiesWithBothComponents_WithCorrectMeshTransformTint()
        {
            var w = new World();

            var a = w.Spawn();
            w.Set(a, new Transform3D { Position = new Vector3(1, 2, 3), Scale = new Vector3(2, 2, 2) });
            w.Set(a, new MeshInstance { Mesh = new MeshHandle(5), Tint = new Vector4(1, 0, 0, 1) });

            var b = w.Spawn();
            w.Set(b, new Transform3D { Position = Vector3.Zero });          // no MeshInstance -> skipped

            var c = w.Spawn();
            w.Set(c, new MeshInstance { Mesh = new MeshHandle(2) });        // no Transform3D -> skipped

            var d = w.Spawn();
            w.Set(d, new Transform3D { Position = new Vector3(4, 0, 0) });
            w.Set(d, new MeshInstance { Mesh = new MeshHandle(7) });         // zero tint -> white

            var drawn = new List<(int mesh, Matrix4x4 mat, Vector4 tint)>();
            Scene3DBinder.Submit(w, (mesh, mat, tint) => drawn.Add((mesh.Index, mat, tint)));

            Assert.Equal(2, drawn.Count);

            var da = drawn.Find(x => x.mesh == 5);
            Assert.Equal(new Vector4(1, 0, 0, 1), da.tint);
            Assert.Equal(new Vector3(1, 2, 3), da.mat.Translation);
            Assert.Equal(2f, da.mat.M11, 4);                                // scale applied

            var dd = drawn.Find(x => x.mesh == 7);
            Assert.Equal(Vector4.One, dd.tint);                            // zero tint -> white
            Assert.Equal(new Vector3(4, 0, 0), dd.mat.Translation);
        }

        [Fact]
        public void MeshInstance_DefaultMaterial_IsMatte_LikeNone()
        {
            var m = new MeshInstance { Mesh = new MeshHandle(0) };
            Assert.Equal(Vector4.Zero, m.Material.Emissive);
            Assert.Equal(0f, m.Material.Specular);
        }

        [Fact]
        public void Submit_CarriesMaterialThrough()
        {
            var w = new World();

            var glow = new Vector4(0.9f, 0.4f, 0.1f, 1f);
            var a = w.Spawn();
            w.Set(a, new Transform3D { Position = new Vector3(1, 0, 0) });
            w.Set(a, new MeshInstance { Mesh = new MeshHandle(5), Material = Material.Glowing(glow) });

            var b = w.Spawn();
            w.Set(b, new Transform3D { Position = new Vector3(2, 0, 0) });
            w.Set(b, new MeshInstance { Mesh = new MeshHandle(6), Material = Material.Shiny(0.6f, 64f) });

            var c = w.Spawn();
            w.Set(c, new Transform3D { Position = new Vector3(3, 0, 0) });
            w.Set(c, new MeshInstance { Mesh = new MeshHandle(7) });          // default material

            var drawn = new List<(int mesh, Material mat)>();
            Scene3DBinder.Submit(w, (mesh, mat, tint, material) => drawn.Add((mesh.Index, material)));

            Assert.Equal(3, drawn.Count);

            var da = drawn.Find(x => x.mesh == 5);
            Assert.Equal(glow, da.mat.Emissive);
            Assert.Equal(0f, da.mat.Specular);

            var db = drawn.Find(x => x.mesh == 6);
            Assert.Equal(Vector4.Zero, db.mat.Emissive);
            Assert.Equal(0.6f, db.mat.Specular, 4);
            Assert.Equal(64f, db.mat.Shininess, 4);

            var dc = drawn.Find(x => x.mesh == 7);
            Assert.Equal(Vector4.Zero, dc.mat.Emissive);
            Assert.Equal(0f, dc.mat.Specular);
        }

        [Fact]
        public void Transform3D_ToMatrix_PositionOnly_DefaultsScaleAndRotationToIdentity()
        {
            var m = new Transform3D { Position = new Vector3(2, 3, 4) }.ToMatrix();
            Assert.Equal(new Vector3(2, 3, 4), m.Translation);
            Assert.Equal(1f, m.M11, 4);
            Assert.Equal(1f, m.M22, 4);
            Assert.Equal(1f, m.M33, 4);
        }
    }
}
