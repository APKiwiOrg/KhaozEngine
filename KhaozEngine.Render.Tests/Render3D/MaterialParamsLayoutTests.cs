using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The model material params block (issue #1776): its C# mirror and the GLSL block agree byte for byte, and
/// every fragment on the two model material layouts declares the same block, or the emissive colour is read from the
/// wrong bytes.</summary>
public sealed class MaterialParamsLayoutTests
{
    [Fact]
    public void MaterialParams_IsOneStd140Vec4_DeclaredByEveryModelFragment()
    {
        Assert.Equal((int)ModelRenderer.MaterialParamsUbo.SizeInBytes, Marshal.SizeOf<ModelRenderer.MaterialParamsUbo>());
        Assert.Equal(0, (int)Marshal.OffsetOf<ModelRenderer.MaterialParamsUbo>(nameof(ModelRenderer.MaterialParamsUbo.Emissive)));
        foreach (var (name, src) in new[]
        {
            ("ModelFrag", ShaderSources.ModelFrag), ("ModelDissolveFrag", ShaderSources.ModelDissolveFrag),
            ("SkinnedModelFrag", ShaderSources.SkinnedModelFrag),
            ("SkinnedModelDissolveFrag", ShaderSources.SkinnedModelDissolveFrag),
        })
            Assert.True(src.Contains("uniform MaterialParams { vec4 MaterialEmissive; };"),
                $"{name} does not declare the MaterialParams block its material set binds.");
    }
}
