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
    }
}
