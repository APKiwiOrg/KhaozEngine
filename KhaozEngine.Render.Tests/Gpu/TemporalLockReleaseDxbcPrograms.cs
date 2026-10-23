using System;
using System.Linq;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Every release-bearing shipped program, including debug/count and both certified precision forms.
internal static class TemporalLockReleaseDxbcPrograms
{
    internal static readonly (string Name, string Source)[] All = ShippedShaderPrograms.GraphicsPrograms()
        .Where(p => p.FragmentGlsl.Contains(ShaderSources.TemporalLockReleaseGlsl, StringComparison.Ordinal))
        .Select(p => (p.Name + "Frag", p.FragmentGlsl)).ToArray();

    // The six ordinary resolve/accumulate programs already have paired D-prime association fixtures.
    internal static readonly (string Name, string Source)[] Additional = All
        .Where(p => !D3D11FxcValidationTests.VisibilityPrograms.Any(v => v.Name == p.Name)).ToArray();
}
