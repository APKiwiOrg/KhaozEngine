using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// THE FRAME VIEW IS THE ONLY CAMERA READ (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 1 and risk
/// 1). Every pass in KhaozEngine.Render3D reads its matrices from <c>Scene3D.CurrentFrameView</c>, which carries the
/// jitter for everything rasterised into the internal target and the unjittered matrices for everything else. A pass
/// that reads the camera instead draws unjittered, which round 2 would show as one layer shimmering against the rest,
/// so a new one fails here rather than on a player's screen.
/// <para>
/// A text sweep in the style of <c>MetalCopyBufferCallSiteTests</c>: it blanks comments, then looks for a camera
/// matrix member (<c>View</c>, <c>Projection</c>, <c>ViewProjection</c>, <c>AbsoluteViewProjection</c>) read off
/// <c>ActiveCamera</c>, <c>CameraOverride</c>, <c>Camera</c>, or any identifier the same file declares with a camera
/// type, and for any call of the two render-origin helpers that build the frame's matrices. Only the latch
/// (Scene3D.FrameView.cs), the helpers it calls (Scene3D.RenderOrigin.cs) and the cameras themselves (Camera/) may do
/// either.
/// </para>
/// <para>
/// WHAT IT DOES NOT SEE: a camera reached through an identifier the file does not declare with a camera type, or a
/// matrix the latch copies out and hands on. The controls below prove the sweep reads the package and that its matcher
/// fires on the latch's own reads, so a clean report is not a dead matcher.
/// </para>
/// </summary>
public sealed class FrameViewConsumerSweepTests
{
    static readonly string[] LatchFiles = { "Scene3D.FrameView.cs", "Scene3D.RenderOrigin.cs" };
    static readonly string[] CameraTypes = { "IIsoCamera3D", "IRenderOriginAware", "IsoCamera3D", "FollowCamera3D", "FlyCamera3D" };
    static readonly string[] WellKnownReceivers = { "ActiveCamera", "CameraOverride", "Camera" };
    const string MatrixMembers = "AbsoluteViewProjection|ViewProjection|Projection|View";
    const string HelperCall = @"\bFrame(?:Absolute)?ViewProjection\s*\(";

    readonly ITestOutputHelper _output;

    public FrameViewConsumerSweepTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void NoPassReadsTheCameraOutsideTheFrameViewLatch()
    {
        string root = SourceSweep.Render3DRoot();
        var violations = new List<string>();
        foreach ((string path, string text) in SourceSweep.Render3DSources())
        {
            string[] segments = SourceSweep.Segments(root, path);
            if (segments[0] == "Camera") continue;
            if (segments.Length == 1 && LatchFiles.Contains(segments[0])) continue;
            violations.AddRange(Reads(path, text));
        }
        Assert.True(violations.Count == 0,
            "These read the camera's matrices instead of Scene3D.CurrentFrameView. A pass that rasterises into the "
            + "internal target takes the jittered side, CPU work and passes drawn at display size after post take the "
            + "unjittered side (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 1):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void TheSweepReadsRender3DRatherThanAnEmptyDirectory()
    {
        List<(string Path, string Text)> sources = SourceSweep.Render3DSources().ToList();
        _output.WriteLine($"{sources.Count} source files under {SourceSweep.Render3DRoot()}");
        Assert.True(sources.Count > 150,
            $"the sweep found only {sources.Count} files, so the producer is broken rather than the package clean");
        Assert.Contains(sources, source => Path.GetFileName(source.Path) == "Scene3D.cs");
    }

    [Fact]
    public void TheMatcherFiresOnTheLatchAndTheHelpersItCalls()
    {
        string root = SourceSweep.Render3DRoot();
        List<string> latch = Reads(Path.Combine(root, "Scene3D.FrameView.cs"), SourceSweep.Render3DSource("Scene3D.FrameView.cs"));
        List<string> helpers = Reads(Path.Combine(root, "Scene3D.RenderOrigin.cs"), SourceSweep.Render3DSource("Scene3D.RenderOrigin.cs"));
        foreach (string read in latch.Concat(helpers)) _output.WriteLine(read);
        Assert.True(latch.Count >= 4, $"the matcher found {latch.Count} camera reads in the latch, expected at least 4");
        Assert.True(helpers.Count >= 5, $"the matcher found {helpers.Count} camera reads in the helpers, expected at least 5");
    }

    /// <summary>Every camera matrix read and render-origin helper call in one comment-blanked file.</summary>
    internal static List<string> Reads(string path, string text)
    {
        var receivers = new HashSet<string>(WellKnownReceivers, StringComparer.Ordinal);
        foreach (Match declaration in Regex.Matches(text, @"\b(?:" + string.Join("|", CameraTypes) + @")\??\s+(?<name>[A-Za-z_]\w*)"))
            receivers.Add(declaration.Groups["name"].Value);
        string read = @"\b(?<receiver>" + string.Join("|", receivers.Select(Regex.Escape)) + @")\s*\??\.\s*(?<member>"
            + MatrixMembers + @")\b";
        string file = Path.GetFileName(path);
        var hits = new List<string>();
        foreach (Match match in Regex.Matches(text, read))
            hits.Add($"{file}:{SourceSweep.Line(text, match.Index)}  {match.Groups["receiver"].Value}.{match.Groups["member"].Value}");
        foreach (Match match in Regex.Matches(text, HelperCall))
            hits.Add($"{file}:{SourceSweep.Line(text, match.Index)}  {match.Value.TrimEnd('(', ' ', '\t')}()");
        return hits;
    }
}
