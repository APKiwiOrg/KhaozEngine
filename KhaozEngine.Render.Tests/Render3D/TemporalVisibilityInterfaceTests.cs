using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("NativeDeviceLifecycle")]
public sealed class TemporalVisibilityInterfaceTests
{
    [Theory]
    [InlineData("TexturedBillboard", false)]
    [InlineData("TexturedBillboard", true)]
    [InlineData("Beam", false)]
    [InlineData("Beam", true)]
    [InlineData("Trail", false)]
    [InlineData("Trail", true)]
    [InlineData("OverlayUnlit", false)]
    [InlineData("OverlayUnlit", true)]
    [InlineData("Silhouette", false)]
    [InlineData("Silhouette", true)]
    public void Preserving_fragments_declare_every_attached_model_target(string stem, bool motion)
    {
        string name = stem + (motion ? "MotionFrag" : "Frag");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        string source = (string)(typeof(ShaderSources).GetProperty(name, flags)?.GetValue(null)
            ?? typeof(ShaderSources).GetField(name, flags)!.GetValue(null))!;
        int[] locations = Regex.Matches(source, @"layout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*out\b")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToArray();
        int attachments = 3 + (motion ? 1 : 0) + (TemporalVisibilityDiagnostics.Enabled ? 1 : 0);
        Assert.Equal(Enumerable.Range(0, attachments), locations);
    }
}
