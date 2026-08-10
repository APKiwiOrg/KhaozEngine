using System;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A RESOURCE A BIND CAN READ, WITH NOTHING UNDER IT. <see cref="IMetalBindable"/> is the whole surface the
    /// bind flush reaches (a handle and a ring), so a texture and a sampler in a device-free bind test are two
    /// numbers rather than <c>MTLTexture</c>s nobody can make on Linux.
    /// <para>
    /// BOTH VALUES ARE SETTABLE, because their whole job is to CHANGE on disposal: the real wrappers answer
    /// <see cref="IntPtr.Zero"/> and null once released, which is what a bind of a resource disposed since its
    /// set was created degrades to. Buffers in these tests come from <see cref="MetalRingHarness"/> instead, so
    /// the ring composition runs against a real <see cref="MetalUniformRing"/> rather than a stand-in.
    /// </para>
    /// </summary>
    internal sealed class FakeMetalBindable : IMetalBindable
    {
        internal FakeMetalBindable(int handle) => BindHandle = new IntPtr(handle);

        public IntPtr BindHandle { get; set; }

        public MetalUniformRing? BindRing { get; set; }
    }

    /// <summary>
    /// THE PROGRAM THE BIND TESTS BIND THROUGH, and it is a REAL index table read out of a real MSL emission by
    /// the shipped mechanism (<c>MetalShaderBuild</c>), not a table a test hand-built.
    ///
    /// <para><b>WHY A REAL ONE.</b> The property the flush has to get right is that a stage with NO ENTRY for an
    /// element is not bound for that element, and that indices are a fact about the emission rather than about
    /// declaration order (2.2b). A hand-built table would let a test assert both while the shipped path did
    /// something else, which is the failure phase 2's byte-equality header records in its own words. This shape
    /// is chosen so the emission itself produces the partial-stage case: the vertex stage reads binding 0 alone
    /// and the fragment stage reads all four, so three of the eight stage/element slots are unreferenced, which
    /// is the same ratio the shipped corpus has (95 of 254).</para>
    ///
    /// <para><b>THE SET SHAPE IS THE ENGINE'S MODEL SHAPE IN MINIATURE:</b> a per-frame uniform buffer read by
    /// both stages and declared DYNAMIC, a second uniform buffer, a texture and a sampler. That is what makes the
    /// budget numbers here mean what section 6.3 says they mean.</para>
    /// </summary>
    internal static class MetalBindProgram
    {
        /// <summary>Set 0, binding 0: a uniform buffer BOTH stages read, and the one element declared
        /// dynamic.</summary>
        internal const int FrameBinding = 0;

        /// <summary>Set 0, binding 1: a uniform buffer only the fragment stage reads.</summary>
        internal const int MaterialBinding = 1;

        /// <summary>Set 0, binding 2: a texture only the fragment stage reads.</summary>
        internal const int TextureBinding = 2;

        /// <summary>Set 0, binding 3: a sampler only the fragment stage reads.</summary>
        internal const int SamplerBinding = 3;

        internal const string VertexGlsl = @"#version 450
layout(set=0, binding=0) uniform Frame { mat4 ViewProj; };
layout(location=0) in vec3 Pos;
void main() { gl_Position = ViewProj * vec4(Pos, 1.0); }
";

        internal const string FragmentGlsl = @"#version 450
layout(set=0, binding=0) uniform Frame { mat4 ViewProj; };
layout(set=0, binding=1) uniform Material { vec4 Tint; };
layout(set=0, binding=2) uniform texture2D Albedo;
layout(set=0, binding=3) uniform sampler Samp;
layout(location=0) out vec4 oColor;
void main() { oColor = texture(sampler2D(Albedo, Samp), vec2(0.5)) * Tint * ViewProj[0]; }
";

        /// <summary>The real table for the pair above, built by the shipped path.</summary>
        internal static MetalShaderIndexTable Table()
            => MetalShaderBuild.Pair(VertexGlsl, FragmentGlsl, "MetalBindProgram").Table;

        /// <summary>
        /// A set matching that program's layout: two ring-backed buffers from <paramref name="harness"/> and a
        /// fabricated texture and sampler. Binding 0 applies the caller's per-draw offset, which is the one thing
        /// <c>GpuResourceLayoutElement.Dynamic</c> decides on this backend.
        /// </summary>
        /// <param name="harness">Where the two real rings come from.</param>
        /// <param name="frameBytes">The logical size of the dynamic uniform buffer. 64 rounds to a 256-byte
        /// segment stride, which leaves room for a caller offset and a window that does not fit it.</param>
        internal static MetalBoundSet Set(MetalRingHarness harness, uint frameBytes = 64)
        {
            MetalBuffer frame = harness.NewBuffer(frameBytes, KhaozEngine.Gpu.GpuBufferUsage.UniformBuffer);
            MetalBuffer material = harness.NewBuffer(32, KhaozEngine.Gpu.GpuBufferUsage.UniformBuffer);

            return Set(
                new MetalBoundResource(MetalIndexSpace.Buffer, frame, 0, frameBytes, AppliesCallerOffset: true),
                new MetalBoundResource(MetalIndexSpace.Buffer, material, 0, 32, AppliesCallerOffset: false),
                new MetalBoundResource(MetalIndexSpace.Texture, new FakeMetalBindable(0x7E11), 0, 0, false),
                new MetalBoundResource(MetalIndexSpace.Sampler, new FakeMetalBindable(0x5A11), 0, 0, false));
        }

        /// <summary>The same shape with the resources a caller chooses, so a test can hand in a disposed one or a
        /// second distinct set.</summary>
        internal static MetalBoundSet Set(params MetalBoundResource[] bindings)
        {
            bool dynamic = false;
            foreach (MetalBoundResource binding in bindings) dynamic |= binding.AppliesCallerOffset;

            return new MetalBoundSet(bindings, dynamic);
        }
    }
}
