using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The released <c>BindNativeAssets</c> signatures stay binary compatible beside the provider overloads.</summary>
public class BindNativeAssetsSignatureTests
{
    [Fact]
    public void ReleasedSignatures_StillExistWithTheirExactParameterTypes()
    {
        AssertPublicVoid(typeof(EditorDocument), typeof(MapAssetClosure));
        AssertPublicVoid(typeof(EditorHistory), typeof(MapDocument), typeof(MapAssetClosure));
    }

    [Fact]
    public void ProviderOverloads_TakeTheProviderWithoutAnOptionalParameter()
    {
        AssertPublicVoid(typeof(EditorDocument), typeof(MapAssetClosure), typeof(INativePlacementBounds));
        AssertPublicVoid(typeof(EditorHistory), typeof(MapDocument), typeof(MapAssetClosure), typeof(INativePlacementBounds));
    }

    static void AssertPublicVoid(Type type, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod("BindNativeAssets", BindingFlags.Public | BindingFlags.Instance, parameters);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Equal(parameters, method.GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(method.GetParameters(), p => p.IsOptional);
    }
}
