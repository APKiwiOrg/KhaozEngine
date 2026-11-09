using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Tests.Render3D;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanExtensionCallSiteTests
{
    // #1539: Silk infers the extension from T. The optional string filters by LAYER, not extension.
    [Fact]
    public void EveryExtensionLookup_LeavesTheLayerFilterUnset()
    {
        string root = Path.Combine(SourceSweep.RepositoryRoot(), "KhaozEngine.Gpu.Vulkan");
        var sites = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !SourceSweep.Segments(root, path).Any(part => part is "bin" or "obj"))
            .SelectMany(path => Lookups(File.ReadAllText(path)).Select(site => (Path: path, Site: site)))
            .ToArray();

        Assert.NotEmpty(sites);
        Assert.Contains(sites, site => Path.GetFileName(site.Path) == "VulkanDebugMessenger.cs");
        Assert.All(sites, site => Assert.True(LayerIsUnset(site.Site),
            $"{site.Path}:{site.Site.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: "
            + "Silk extension lookups must omit the layer filter or pass null. " + site.Site));
    }

    [Theory]
    [InlineData("vk.TryGetInstanceExtension(instance, out ExtDebugUtils ext)", true)]
    [InlineData("vk.TryGetDeviceExtension(instance, device, out KhrSwapchain ext, null)", true)]
    [InlineData("vk.TryGetInstanceExtension<ExtDebugUtils>(instance, out ext, layer: null)", true)]
    [InlineData("vk.TryGetInstanceExtension(instance, out ExtDebugUtils ext, ExtDebugUtils.ExtensionName)", false)]
    [InlineData("vk.TryGetDeviceExtension(instance, device, out KhrSwapchain ext, layer: name)", false)]
    [InlineData("vk.TryGetInstanceExtension(layer: \"VK_EXT_debug_utils\", ext: out ext, instance: instance)", false)]
    public void TheGuard_DistinguishesTheLayerArgument(string source, bool accepted)
        => Assert.Equal(accepted, LayerIsUnset(Assert.Single(Lookups(source))));

    static IEnumerable<InvocationExpressionSyntax> Lookups(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(site => site.Expression is MemberAccessExpressionSyntax access
                && access.Name.Identifier.ValueText is "TryGetInstanceExtension" or "TryGetDeviceExtension");

    static bool LayerIsUnset(InvocationExpressionSyntax site)
    {
        string name = ((MemberAccessExpressionSyntax)site.Expression).Name.Identifier.ValueText;
        int layerIndex = name == "TryGetInstanceExtension" ? 2 : 3;
        SeparatedSyntaxList<ArgumentSyntax> arguments = site.ArgumentList.Arguments;
        ArgumentSyntax? layer = arguments.FirstOrDefault(arg => arg.NameColon?.Name.Identifier.ValueText == "layer");
        if (layer is null && arguments.Count > layerIndex && arguments[layerIndex].NameColon is null)
            layer = arguments[layerIndex];
        return layer is null || layer.Expression.IsKind(SyntaxKind.NullLiteralExpression);
    }
}
