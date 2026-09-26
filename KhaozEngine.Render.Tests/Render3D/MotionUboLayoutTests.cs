using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The motion blocks' C# and GLSL halves, and the list of temporal programs that declare the shared frame block
/// <c>U</c>. <see cref="UboLayoutTests"/> runs its frame-block sweeps over <see cref="FrameBlockSources"/> as well, so a
/// motion variant that drifts from <c>U</c> fails there. That file is at the size cap, which is why the list lives here.
/// </summary>
public sealed class MotionUboLayoutTests
{
    /// <summary>Every temporal program that declares <c>U</c>. Each task that adds a variant adds its entries.</summary>
    internal static readonly (string Name, string Source)[] FrameBlockSources = [];

    [Fact]
    public void MotionFrameUboMirrorsTheGlslBlock()
    {
        Assert.Equal(144u, MotionFrameUbo.SizeInBytes);
        Assert.Equal((int)MotionFrameUbo.SizeInBytes, Marshal.SizeOf<MotionFrameUbo>());
        Assert.Equal(0, (int)Marshal.OffsetOf<MotionFrameUbo>(nameof(MotionFrameUbo.CurViewProj)));
        Assert.Equal(64, (int)Marshal.OffsetOf<MotionFrameUbo>(nameof(MotionFrameUbo.PrevViewProj)));
        Assert.Equal(128, (int)Marshal.OffsetOf<MotionFrameUbo>(nameof(MotionFrameUbo.Params)));
        Assert.Equal(["mat4 CurViewProj", "mat4 PrevViewProj", "vec4 MotionParams"],
            Members("uniform MotionFrame {" + ShaderSources.MotionFrameMembersGlsl + "};", "uniform MotionFrame {"));
    }

    [Fact]
    public void TheCompactFrameBlockDeclaresModelVertsMembersInOrder()
        => Assert.Equal(Members(ShaderSources.ModelVert, "uniform U {"),
            Members(ShaderSources.CompactFrameBlockGlsl, "uniform U {"));

    /// <summary>The member declarations of the block opened by <paramref name="open"/>, comments stripped and
    /// whitespace collapsed.</summary>
    internal static string[] Members(string source, string open)
    {
        int at = source.IndexOf(open, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The block '{open}' is not in the source.");
        int start = at + open.Length;
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string body = Regex.Replace(source[start..end], "//[^\n]*", "");
        return body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => Regex.Replace(m, @"\s+", " "))
            .ToArray();
    }
}
