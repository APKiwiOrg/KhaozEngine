using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // LoadNamedNodes reads every named glTF node, empties included, with its world transform. Consumers use it to
    // attach modules, muzzles or exhaust points at authored socket empties, so the transform must be the same one
    // Load bakes into geometry and the order must be the stable logical-node order.
    public class GltfLoaderNamedNodeTests
    {
        static readonly Vector3 TriA = new(0.5f, 0f, 0f);
        static readonly Vector3 TriB = new(2f, 0.25f, 0f);
        static readonly Vector3 TriC = new(0.75f, 1.5f, 0.5f);

        static ModelRoot NewModel(out Scene scene)
        {
            ModelRoot model = ModelRoot.CreateModel();
            scene = model.UseScene("scene");
            return model;
        }

        static Mesh TriangleMesh(ModelRoot model)
        {
            var builder = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("tri");
            builder.UsePrimitive(MaterialBuilder.CreateDefault())
                .AddTriangle(new VertexPosition(TriA), new VertexPosition(TriB), new VertexPosition(TriC));
            return model.CreateMesh(builder);
        }

        static string Save(ModelRoot model)
        {
            string path = Path.Combine(Path.GetTempPath(), $"ke_named_{Guid.NewGuid():N}.glb");
            model.SaveGLB(path);
            return path;
        }

        static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
        {
            float[] e =
            {
                expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23,
                expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42,
                expected.M43, expected.M44,
            };
            float[] a =
            {
                actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
                actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44,
            };
            for (int i = 0; i < e.Length; i++)
                Assert.True(MathF.Abs(e[i] - a[i]) < 1e-5f, $"matrix element {i}: expected {expected}, got {actual}");
        }

        [Fact]
        public void Empty_IsReturned_WithWorldTransform()
        {
            ModelRoot model = NewModel(out Scene scene);
            Node hull = scene.CreateNode("hull");
            hull.Mesh = TriangleMesh(model);
            Matrix4x4 nose = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 0.5f, 2.5f);
            scene.CreateNode("socket_nose").LocalMatrix = nose;
            string path = Save(model);
            try
            {
                var nodes = GltfLoader.LoadNamedNodes(path);
                GltfNamedNode socket = Assert.Single(nodes, n => n.Name == "socket_nose");
                AssertMatrixNear(nose, socket.WorldTransform);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Empty_IsReturned_FromAssetWithNoGeometry()
        {
            ModelRoot model = NewModel(out Scene scene);
            scene.CreateNode("socket_tail").LocalMatrix = Matrix4x4.CreateTranslation(0f, 0f, -3f);
            string path = Save(model);
            try
            {
                GltfNamedNode socket = Assert.Single(GltfLoader.LoadNamedNodes(path));
                Assert.Equal("socket_tail", socket.Name);
                AssertMatrixNear(Matrix4x4.CreateTranslation(0f, 0f, -3f), socket.WorldTransform);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ParentChain_IsComposed()
        {
            ModelRoot model = NewModel(out Scene scene);
            Matrix4x4 rootLocal = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f);
            Matrix4x4 wingLocal = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(1f, 0f, 0f);
            Matrix4x4 tipLocal = Matrix4x4.CreateTranslation(0f, 0f, 3f);
            Node root = scene.CreateNode("ship");
            root.LocalMatrix = rootLocal;
            Node wing = root.CreateNode("wing_l");
            wing.LocalMatrix = wingLocal;
            wing.CreateNode("socket_wing_l").LocalMatrix = tipLocal;
            string path = Save(model);
            try
            {
                var byName = GltfLoader.LoadNamedNodes(path).ToDictionary(n => n.Name, n => n.WorldTransform);
                // Row-vector order: a child's world is its local, then each ancestor's local outward to the root.
                AssertMatrixNear(rootLocal, byName["ship"]);
                AssertMatrixNear(wingLocal * rootLocal, byName["wing_l"]);
                AssertMatrixNear(tipLocal * wingLocal * rootLocal, byName["socket_wing_l"]);
                // The wing is rotated a quarter turn about Z and moved to x=10, so its +X offset lands on +Y.
                Assert.True(Vector3.Distance(new Vector3(10f, 1f, 0f), byName["wing_l"].Translation) < 1e-5f,
                    $"wing origin {byName["wing_l"].Translation}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void UnnamedNodes_AreSkipped()
        {
            ModelRoot model = NewModel(out Scene scene);
            Node unnamed = scene.CreateNode();
            unnamed.LocalMatrix = Matrix4x4.CreateTranslation(5f, 0f, 0f);
            scene.CreateNode(string.Empty);
            unnamed.CreateNode("socket_under_unnamed").LocalMatrix = Matrix4x4.CreateTranslation(0f, 1f, 0f);
            scene.CreateNode("socket_top");
            string path = Save(model);
            try
            {
                var nodes = GltfLoader.LoadNamedNodes(path);
                Assert.Equal(new[] { "socket_under_unnamed", "socket_top" }, nodes.Select(n => n.Name));
                Assert.All(nodes, n => Assert.False(string.IsNullOrEmpty(n.Name)));
                // An unnamed parent still places its named child.
                AssertMatrixNear(Matrix4x4.CreateTranslation(5f, 1f, 0f), nodes[0].WorldTransform);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Order_IsLogicalNodeOrder()
        {
            // Creation order is the logical-node order. It is deliberately neither alphabetical nor depth-first.
            ModelRoot model = NewModel(out Scene scene);
            Node zeta = scene.CreateNode("zeta");
            scene.CreateNode("alpha");
            zeta.CreateNode("mid");
            scene.CreateNode("beta");
            string path = Save(model);
            try
            {
                ModelRoot reread = ModelRoot.Load(path);
                string[] logical = reread.LogicalNodes.Select(n => n.Name).ToArray();
                Assert.Equal(new[] { "zeta", "alpha", "mid", "beta" }, logical);
                Assert.Equal(logical, GltfLoader.LoadNamedNodes(path).Select(n => n.Name));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Transform_MatchesGeometryBake()
        {
            ModelRoot model = NewModel(out Scene scene);
            Node root = scene.CreateNode("ship");
            root.LocalMatrix = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(3f, -2f, 1f);
            Node hull = root.CreateNode("hull");
            hull.LocalMatrix = Matrix4x4.CreateScale(1.5f, 0.5f, 2f) * Matrix4x4.CreateRotationX(0.3f)
                * Matrix4x4.CreateTranslation(0f, 4f, 0f);
            hull.Mesh = TriangleMesh(model);
            string path = Save(model);
            try
            {
                Matrix4x4 world = Assert.Single(GltfLoader.LoadNamedNodes(path), n => n.Name == "hull").WorldTransform;
                Vector3[] baked = GltfLoader.Load(path).Vertices.Select(v => v.Position).ToArray();
                Assert.Equal(3, baked.Length);
                // The node transform applied to each local vertex is the exact vertex Load baked.
                foreach (Vector3 local in new[] { TriA, TriB, TriC })
                    Assert.Contains(Vector3.Transform(local, world), baked);
            }
            finally { File.Delete(path); }
        }
    }
}
