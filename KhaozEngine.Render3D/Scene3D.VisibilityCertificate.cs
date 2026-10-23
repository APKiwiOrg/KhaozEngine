namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Evidence for this scene's resolved R8, after the receiver upload all opaque writers share. The white
    /// clear, discarded fragments, destination-preserving writers and MSAA resolve preserve the exact value one.</summary>
    internal bool ResolvedVisibilityFullyLit => _res.LitVisibilityTex is not null && _model.UploadedKeyShadowFullyLit;
}
