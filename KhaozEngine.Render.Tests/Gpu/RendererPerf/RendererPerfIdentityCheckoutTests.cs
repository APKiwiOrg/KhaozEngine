using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfIdentityCheckoutTests
{
    [Fact]
    public void Exact_byte_identity_inputs_require_lf_checkout_attributes()
    {
        var checkout = new RendererPerfCheckout(Root());
        // Include composed shader fragments and uniform declarations, not only complete programs.
        string[] sources = checkout.Git("ls-files", "-z", "--",
            "KhaozEngine.Render3D/Internal/ShaderSources*.cs").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(sources);
        string[] inputs = [.. sources,
            "KhaozEngine.Render3D/Internal/OceanComputeShaders.cs",
            "KhaozEngine.Render3D/Internal/TemporalResolveUniforms.cs",
            "KhaozEngine.Render2D/SpriteBatch.cs",
            "KhaozEngine.Render2D/MaskedSpriteShader.cs",
            "KhaozEngine.Gpu/GpuScaledCopy.cs",
            // Test-side frozen copies and snippets compared byte for byte against the sources above.
            "KhaozEngine.Render.Tests/Gpu/TemporalVisibilityFrozenD.cs",
            "KhaozEngine.Render.Tests/Gpu/TemporalVisibilityFrozenDMask.cs",
            "KhaozEngine.Render.Tests/Gpu/TemporalLockReleaseProbe.cs",
            "KhaozEngine.Render.Tests/Render3D/PointShadowFilterShaderTests.cs",
            .. checkout.Git("ls-files", "-z", "--",
                "KhaozEngine.Render.Tests/Gpu/RendererPerf/controls/*.glsl",
                "tools/Renderer.Perf.Tests/authorized/*.json").Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        Assert.All(inputs, input => Assert.True(File.Exists(Path.Combine(Root(), input)), input));
        string[] attributes = checkout.Git(["check-attr", "-z", "text", "eol", "--", .. inputs])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(inputs.Length * 6, attributes.Length);
        var failures = new List<string>();
        for (int i = 0; i < attributes.Length; i += 3)
        {
            string expected = attributes[i + 1] == "text" ? "set" : "lf";
            if (attributes[i + 2] != expected)
                failures.Add(attributes[i] + ": " + attributes[i + 1] + "=" + attributes[i + 2]);
        }
        Assert.True(failures.Count == 0, "Exact renderer identity inputs must use text eol=lf:\n" + string.Join("\n", failures));
    }

    static string Root([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
}
