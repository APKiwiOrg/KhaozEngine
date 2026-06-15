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
