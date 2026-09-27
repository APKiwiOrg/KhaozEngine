using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The temporal passes' uniform blocks against the sizes their buffers allocate, the std140 offsets and the
    /// GLSL text the shaders declare, beside <c>UboLayoutTests</c>.</summary>
    public sealed class TemporalUboLayoutTests
    {
        [Fact]
        public void TemporalResolveUniforms_MarshalSize_EqualsBufferAllocation()
        {
            // GLSL: Resolve { mat4 CurrentToPrevious; mat4 BackgroundToPrevious; mat4 PreviousProjection; vec4 Sizes;
            // vec4 Jitter; vec4 CurrentDepth; vec4 Params; } = 3 mat4 + 4 vec4 = 256 bytes (TemporalResolveCoreGlsl).
            Assert.Equal((int)TemporalResolveUniforms.SizeInBytes, Marshal.SizeOf<TemporalResolveUniforms>());
            Assert.Equal(256u, TemporalResolveUniforms.SizeInBytes);
        }

        [Fact]
        public void TemporalDepthStoreUniforms_MarshalSize_EqualsBufferAllocation()
        {
            // GLSL: DepthStore { vec4 CurrentDepth; } = 16 bytes (TemporalDepthStoreFrag).
            Assert.Equal((int)TemporalDepthStoreUniforms.SizeInBytes, Marshal.SizeOf<TemporalDepthStoreUniforms>());
        }

        [Fact]
        public void Every_field_sits_at_its_std140_offset()
        {
            // A mat4 is four vec4 columns, 64 bytes at 16-byte alignment, and a vec4 is 16 bytes.
            Assert.Equal(0, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.CurrentToPrevious)));
            Assert.Equal(64, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.BackgroundToPrevious)));
            Assert.Equal(128, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.PreviousProjection)));
            Assert.Equal(192, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.Sizes)));
            Assert.Equal(208, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.Jitter)));
            Assert.Equal(224, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.CurrentDepth)));
            Assert.Equal(240, Offset<TemporalResolveUniforms>(nameof(TemporalResolveUniforms.Params)));
            Assert.Equal(0, Offset<TemporalDepthStoreUniforms>(nameof(TemporalDepthStoreUniforms.CurrentDepth)));
        }

        [Fact]
        public void The_GLSL_members_declare_the_fields_in_order_with_their_types_and_sizes()
        {
            AssertMirrors<TemporalResolveUniforms>(TemporalResolveUniforms.GlslMembers, TemporalResolveUniforms.SizeInBytes);
            AssertMirrors<TemporalDepthStoreUniforms>(TemporalDepthStoreUniforms.GlslMembers, TemporalDepthStoreUniforms.SizeInBytes);
        }

        [Fact]
        public void SharpenUbo_MarshalSize_EqualsSharpenBufferAllocation()
        {
            // GLSL: Sharpen { vec4 Params; } = 1 vec4 = 16 bytes (TemporalSharpenFrag).
            Assert.Equal(16u, TemporalSharpenPass.SharpenBufferBytes);
            Assert.Equal((int)TemporalSharpenPass.SharpenBufferBytes, Marshal.SizeOf<TemporalSharpenPass.SharpenUbo>());
        }

        static void AssertMirrors<T>(string glslMembers, uint sizeInBytes) where T : struct
        {
            Assert.DoesNotContain("//", glslMembers, StringComparison.Ordinal);
            string[] declared = MotionUboLayoutTests.Members("uniform B {" + glslMembers + "};", "uniform B {");
            string[] fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(f => Offset<T>(f.Name))
                .Select(f => GlslType(f.FieldType) + " " + f.Name)
                .ToArray();
            Assert.Equal(fields, declared);
            Assert.Equal((int)sizeInBytes, declared.Sum(m => m.StartsWith("mat4 ", StringComparison.Ordinal) ? 64 : 16));
        }

        static string GlslType(Type type) => type == typeof(Matrix4x4) ? "mat4"
            : type == typeof(Vector4) ? "vec4"
            : throw new InvalidOperationException($"No std140 type for {type}.");

        static int Offset<T>(string field) where T : struct => (int)Marshal.OffsetOf<T>(field);
    }
}
