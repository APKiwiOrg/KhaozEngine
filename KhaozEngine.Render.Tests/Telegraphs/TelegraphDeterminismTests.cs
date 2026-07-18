using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphDeterminismTests
    {
        [Fact]
        public void Resolve_has_no_hidden_state_across_calls()
        {
            // Interleave different inputs; each output must depend ONLY on its own args (no accumulation).
            for (int i = 0; i < 100; i++)
            {
                float p = (i % 10) / 9f;
                var a = TelegraphResolve.Resolve(p, TelegraphStyle.Generic);
                _ = TelegraphResolve.Resolve(0.123f, TelegraphStyle.Fire); // perturb
                var b = TelegraphResolve.Resolve(p, TelegraphStyle.Generic);
                Assert.Equal(a.FillFraction, b.FillFraction, 6);
                Assert.Equal(a.FlashAdd, b.FlashAdd, 6);
                Assert.Equal((Vector4)a.FillColor, (Vector4)b.FillColor);
            }
        }

        [Fact]
        public void Build_mapping_is_pure()
        {
            var a = GroundTelegraphs.BuildArc(new Vector3(1, 0, 1), 3f, 0.5f, 0.2f, 1.1f, 0.4f, TelegraphStyle.Poison);
            var b = GroundTelegraphs.BuildArc(new Vector3(1, 0, 1), 3f, 0.5f, 0.2f, 1.1f, 0.4f, TelegraphStyle.Poison);
            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.FillFraction, b.FillFraction, 6);
            Assert.Equal((Vector4)a.FillColor, (Vector4)b.FillColor);
        }
    }
}
