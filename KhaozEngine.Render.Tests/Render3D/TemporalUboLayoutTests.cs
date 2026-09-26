using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The temporal passes' uniform blocks against the sizes their buffers allocate, beside
    /// <c>UboLayoutTests</c>.</summary>
    public sealed class TemporalUboLayoutTests
    {
        [Fact]
        public void TemporalResolveUniforms_MarshalSize_EqualsBufferAllocation()
        {
            // GLSL: Resolve { mat4 CurrentToPrevious; mat4 BackgroundToPrevious; vec4 Sizes; vec4 Jitter;
            // vec4 CurrentDepth; vec4 PreviousDepth; vec4 Params; } = 2 mat4 + 5 vec4 = 208 bytes (TemporalResolveCoreGlsl).
            Assert.Equal((int)TemporalResolveUniforms.SizeInBytes, Marshal.SizeOf<TemporalResolveUniforms>());
            Assert.Equal(208u, TemporalResolveUniforms.SizeInBytes);
        }

        [Fact]
        public void TemporalDepthStoreUniforms_MarshalSize_EqualsBufferAllocation()
        {
            // GLSL: DepthStore { vec4 CurrentDepth; } = 16 bytes (TemporalDepthStoreFrag).
            Assert.Equal((int)TemporalDepthStoreUniforms.SizeInBytes, Marshal.SizeOf<TemporalDepthStoreUniforms>());
        }
    }
}
