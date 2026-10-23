using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Names exact shader source pairs with the shipped inventory, including generated visibility variants.</summary>
internal static class MotionTargetPrograms
{
    static readonly Dictionary<(string Vertex, string Fragment), string> Names =
        ShippedShaderPrograms.GraphicsPrograms().ToDictionary(p => (p.VertexGlsl, p.FragmentGlsl), p => p.Name);

    internal static string Name(FakeGraphicsPipelineRequest request) =>
        Names.GetValueOrDefault((request.VertexGlsl, request.FragmentGlsl), "unregistered shader program");

    internal static void AssertLayout(FakeGraphicsPipelineRequest request, int outputs)
    {
        Assert.Equal(outputs, request.Description.Outputs.Colour.Length);
        Assert.Equal(outputs, request.Description.BlendAttachments.Length);
        if (outputs >= 4)
            Assert.Equal(GpuPixelFormat.R16G16Float, request.Description.Outputs.Colour[3]);
        else
            Assert.DoesNotContain("oMotion", request.FragmentGlsl);
        if (outputs == 5)
        {
            Assert.Equal(GpuPixelFormat.R8UNorm, request.Description.Outputs.Colour[4]);
            Assert.Contains("layout(location=4) out float oKeyVisibility;", request.FragmentGlsl);
        }
        else
            Assert.DoesNotContain("oKeyVisibility", request.FragmentGlsl);
    }
}
