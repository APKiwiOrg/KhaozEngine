namespace KhaozEngine.Render3D.Internal;

/// <summary>Correspondence requires the same uploaded source, live handle and immutable deformation profile.</summary>
internal readonly record struct SkinnedMotionIdentity(
    int MeshIndex, int Generation, ulong SourceIdentity, SkinningProfile Profile);
