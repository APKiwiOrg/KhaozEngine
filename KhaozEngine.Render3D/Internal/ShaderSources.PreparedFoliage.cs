namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal static string PreparedPointFoliageVert => PointShadowRigidVert
        .Replace("layout(location=11) in vec4 ISpecParams;", "")
        .Replace(" + ISpecParams.x", "")
        .Replace("void main()", AnchorBlock(1) + "void main()")
        .Replace("vWorldPos = world.xyz;", "vWorldPos = world.xyz + AnchorOffset.xyz;")
        .Replace("gl_Position = LightVp * world;", "gl_Position = preparedViewProj(LightVp) * world;");

    static string AnchorBlock(int set) => "layout(set=" + set + @", binding=0) uniform SceneAnchor {
    vec4 AnchorOffset;
    vec4 AnchorAbsolute;
};
mat4 preparedViewProj(mat4 vp) {
    vp[3] = vp * vec4(AnchorOffset.xyz, 1.0);
    return vp;
}
";

    internal static string PreparedFoliage(string source, int anchorSet) => source
        .Replace("vec3 foliageWorld(", AnchorBlock(anchorSet) + "vec3 foliageWorld(")
        .Replace("Model[3].xyz -= RenderOrigin.xyz;", "")
        .Replace("interactor.xyz - RenderOrigin.xyz", "interactor.xyz")
        .Replace("dot(IModel3.xz,", "dot(IModel3.xz + AnchorAbsolute.xz,")
        .Replace("fadeViewProj * vec4(root,", "preparedViewProj(fadeViewProj) * vec4(root,")
        .Replace(": ViewProj * world", ": preparedViewProj(ViewProj) * world")
        .Replace("LightViewProj * world", "preparedViewProj(LightViewProj) * world")
        .Replace("CurViewProj * world", "preparedViewProj(CurViewProj) * world")
        .Replace("PrevViewProj * vec4(lastWorld,", "preparedViewProj(PrevViewProj) * vec4(lastWorld,")
        .Replace("vWorldPos = world.xyz;", "vWorldPos = world.xyz + AnchorOffset.xyz;");
}
