namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    /// <summary>The actual frame upload shared by rigid, GPU/CPU skinning, foliage, splat and tile ground.
    /// sampleKeyShadow returns exactly one before any atlas read at nonpositive strength. All other R8 writers
    /// preserve the white clear or the opaque value. This snapshots the upload, never the requested shadow mode.</summary>
    internal bool UploadedKeyShadowFullyLit { get; private set; }
}
