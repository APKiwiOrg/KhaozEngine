using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SceneInstancesTests
    {
        [Fact]
        public void Begin_Clears_DrawQueues_InOrder()
        {
            var s = new SceneInstances();
            s.Add(new MeshHandle(2), Matrix4x4.CreateTranslation(1, 0, 0), Vector4.One);
            s.Add(new MeshHandle(5), Matrix4x4.CreateTranslation(0, 0, 3), Vector4.One);

            Assert.Equal(2, s.Items.Count);
            Assert.Equal(2, s.Items[0].Mesh.Index);
            Assert.Equal(5, s.Items[1].Mesh.Index);
            Assert.Equal(1f, s.Items[0].World.M41, 4);   // translation X of the first instance

            s.Begin();
            Assert.Empty(s.Items);
        }

        [Fact]
        public void Add_Stores_PerInstance_Tint()
        {
            var s = new SceneInstances();
            var red = new Vector4(1f, 0f, 0f, 1f);
            s.Add(new MeshHandle(0), Matrix4x4.Identity, red);

            Assert.Equal(red, s.Items[0].Tint);
        }

        [Fact]
        public void Add_Without_Material_Defaults_To_None()
        {
            var s = new SceneInstances();
            s.Add(new MeshHandle(0), Matrix4x4.Identity, Vector4.One);

            Assert.Equal(Vector4.Zero, s.Items[0].Material.Emissive);
            Assert.Equal(0f, s.Items[0].Material.Specular);
            Assert.Equal(32f, s.Items[0].Material.Shininess, 4);
        }

        [Fact]
        public void Add_Stores_PerInstance_Material()
        {
            var s = new SceneInstances();
            var glow = new Vector4(0.8f, 0.2f, 0.1f, 1f);
            s.Add(new MeshHandle(3), Matrix4x4.Identity, Vector4.One, Material.Glowing(glow));

            Assert.Equal(glow, s.Items[0].Material.Emissive);
            Assert.Equal(0f, s.Items[0].Material.Specular);
        }

        [Fact]
        public void Material_None_Is_Matte()
        {
            var m = Material.None;
            Assert.Equal(Vector4.Zero, m.Emissive);
            Assert.Equal(0f, m.Specular);
            Assert.Equal(32f, m.Shininess, 4);
        }

        [Fact]
        public void Material_Emissive_Glows_No_Specular()
        {
            var c = new Vector4(0.1f, 0.9f, 0.3f, 1f);
            var m = Material.Glowing(c);
            Assert.Equal(c, m.Emissive);
            Assert.Equal(0f, m.Specular);
            Assert.Equal(32f, m.Shininess, 4);
        }

        [Fact]
        public void Material_Shiny_Specular_No_Glow()
        {
            var m = Material.Shiny(0.7f, 64f);
            Assert.Equal(Vector4.Zero, m.Emissive);
            Assert.Equal(0.7f, m.Specular, 4);
            Assert.Equal(64f, m.Shininess, 4);
        }

        [Fact]
        public void Material_Shiny_Default_Shininess_Is_48()
        {
            var m = Material.Shiny(0.5f);
            Assert.Equal(48f, m.Shininess, 4);
        }
    }
}
