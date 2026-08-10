using KhaozEngine.Gpu;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH OF METAL'S THREE ARGUMENT TABLES AN INDEX BELONGS TO. A function gets
    /// <c>[[buffer(n)]]</c>, <c>[[texture(n)]]</c> and <c>[[sampler(n)]]</c> as three INDEPENDENT spaces per
    /// stage, so index 0 means three different things and a bare integer would be ambiguous everywhere it is
    /// carried (section 8.1).
    /// <para>
    /// THE SPACE IS ALSO A CHECK RATHER THAN ONLY A LABEL. The binding table resolves an emitted argument to a
    /// declared layout element, and the element's <see cref="GpuResourceKind"/> must agree with the space the
    /// argument landed in. A texture element resolved from a <c>[[buffer(n)]]</c> argument means the join went
    /// wrong, and <see cref="MetalIndexSpaces.MatchesKind"/> is what turns that into a throw rather than a wrong
    /// bind (2.2b, pin 1).
    /// </para>
    /// </summary>
    internal enum MetalIndexSpace
    {
        /// <summary><c>[[buffer(n)]]</c>: uniform buffers and both structured kinds, which SHARE this space.
        /// Vertex streams live here too, pinned at the top by M-B2 so they cannot collide with these.</summary>
        Buffer = 0,

        /// <summary><c>[[texture(n)]]</c>: both texture kinds.</summary>
        Texture = 1,

        /// <summary><c>[[sampler(n)]]</c>: samplers, which have a space to themselves.</summary>
        Sampler = 2,
    }

    /// <summary>The kind-to-space rule, in ONE place, because the parse, the table's own consistency check and
    /// every future binder all have to agree on it.</summary>
    internal static class MetalIndexSpaces
    {
        /// <summary>The space a declared resource of <paramref name="kind"/> is bound in.</summary>
        internal static MetalIndexSpace For(GpuResourceKind kind) => kind switch
        {
            GpuResourceKind.UniformBuffer => MetalIndexSpace.Buffer,
            GpuResourceKind.StructuredBufferReadOnly => MetalIndexSpace.Buffer,
            GpuResourceKind.StructuredBufferReadWrite => MetalIndexSpace.Buffer,
            GpuResourceKind.TextureReadOnly => MetalIndexSpace.Texture,
            GpuResourceKind.TextureReadWrite => MetalIndexSpace.Texture,
            _ => MetalIndexSpace.Sampler,
        };

        /// <summary>Whether a resource of <paramref name="kind"/> belongs in <paramref name="space"/>.</summary>
        internal static bool MatchesKind(this MetalIndexSpace space, GpuResourceKind kind) => For(kind) == space;

        /// <summary>The MSL attribute word for a space, for a message that has to quote the emission back.</summary>
        internal static string Word(this MetalIndexSpace space) => space switch
        {
            MetalIndexSpace.Buffer => "buffer",
            MetalIndexSpace.Texture => "texture",
            _ => "sampler",
        };
    }
}
