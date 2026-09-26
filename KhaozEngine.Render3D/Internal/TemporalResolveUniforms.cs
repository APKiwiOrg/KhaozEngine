using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal resolve's uniform block, mirroring <c>Resolve</c> in <c>ShaderSources.TemporalResolveCoreGlsl</c>.
    /// std140: three mat4 then five vec4, 272 bytes. Built once per frame by <see cref="TemporalResolveMath.BuildUniforms"/>
    /// and uploaded before any framebuffer is bound. Every matrix is System.Numerics bytes, which the shader applies as
    /// <c>M * v</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TemporalResolveUniforms
    {
        public const uint SizeInBytes = 272;

        /// <summary>The block's members in GLSL, in field order, which the resolve shader declares inside its
        /// <c>Resolve</c> block. <c>TemporalUboLayoutTests</c> holds them to the fields.</summary>
        public const string GlslMembers = @"
    mat4 CurrentToPrevious;
    mat4 BackgroundToPrevious;
    mat4 PreviousProjection;
    vec4 Sizes;
    vec4 Jitter;
    vec4 CurrentDepth;
    vec4 PreviousDepth;
    vec4 Params;
";

        /// <summary>This frame's surface point to last frame's view space, render-relative, the previous view already
        /// rebased to this frame's origin. The shader multiplies <c>(ndc.x * w, ndc.y * w, linear depth, 1)</c>, with the
        /// pixel's unjittered NDC and w the linear depth under perspective and 1 under orthographic, and takes minus z as
        /// the expected previous depth (<see cref="TemporalResolveMath.ExpectedPreviousDepth"/>). It assumes the point did
        /// not move, so the resolve applies it only for that expected depth and for the static previous position
        /// (<see cref="PreviousProjection"/>), never for the previous position it reads history at, which comes from the
        /// motion target, and skips the depth test on a surface that moved.</summary>
        public Matrix4x4 CurrentToPrevious;
        /// <summary>This frame's unjittered NDC on the far plane to last frame's clip space, with both views' translation
        /// removed, so background reprojects from camera rotation alone.</summary>
        public Matrix4x4 BackgroundToPrevious;
        /// <summary>Last frame's unjittered projection, applied as <c>M * v</c> to the view-space point from
        /// <see cref="CurrentToPrevious"/>. That gives the UV a static point had last frame
        /// (<see cref="TemporalResolveMath.StaticPreviousUv"/>), and a dilated texel whose motion carries its own sample
        /// more than <see cref="TemporalResolveTuning.MovingSurfaceInternalPixels"/>, plus
        /// <see cref="TemporalResolveTuning.MovingSurfaceMotionFraction"/> of that motion, from it is a moving surface,
        /// which skips the depth test. The current projection when history is not readable, so the block stays well defined.</summary>
        public Matrix4x4 PreviousProjection;
        /// <summary>(internal width, internal height, display width, display height).</summary>
        public Vector4 Sizes;
        /// <summary>(jitter x, jitter y in internal pixels, display over internal per axis, 1 when history may be read).</summary>
        public Vector4 Jitter;
        /// <summary>(1 for a perspective projection else 0, near, far, 0) of this frame.</summary>
        public Vector4 CurrentDepth;
        /// <summary>The same of last frame, which the stored previous depth was linearised with. The resolve does not
        /// read it: the expected depth comes from <see cref="CurrentToPrevious"/> in last frame's view space, which
        /// needs no depth parameters. It is kept so the block layout stays fixed, and for a later reader of the stored
        /// depth, such as a debug view, that needs last frame's near and far.</summary>
        public Vector4 PreviousDepth;
        /// <summary>x: the thin feature lock's decay per frame. yzw reserved.</summary>
        public Vector4 Params;
    }

    /// <summary>The depth store's uniform block, mirroring <c>DepthStore</c> in
    /// <c>ShaderSources.TemporalDepthStoreFrag</c>. 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TemporalDepthStoreUniforms
    {
        public const uint SizeInBytes = 16;

        /// <summary>The block's members in GLSL, which the depth store shader declares inside its <c>DepthStore</c>
        /// block. <c>TemporalUboLayoutTests</c> holds them to the field.</summary>
        public const string GlslMembers = @"
    vec4 CurrentDepth;
";

        /// <summary>(1 for a perspective projection else 0, near, far, 0) of this frame.</summary>
        public Vector4 CurrentDepth;
    }

    /// <summary>The three unjittered matrices of one frame view that the resolve reprojects with. The scene reads them
    /// from a <see cref="FrameView"/>, the previous one already rebased to this frame's render origin, and the tests
    /// build them directly.</summary>
    internal readonly record struct TemporalViewInput(Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProjection);
}
