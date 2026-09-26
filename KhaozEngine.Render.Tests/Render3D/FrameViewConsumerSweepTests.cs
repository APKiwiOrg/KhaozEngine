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
        // One named read per matcher arm, so dropping an arm fails here instead of leaving the sweep blind.
        AssertHit(helpers, "ActiveCamera.ViewProjection", "a well-known receiver");
        AssertHit(helpers, "aware.AbsoluteViewProjection", "an identifier a type pattern declares");
        AssertHit(latch, "cam.View", "an identifier a local declaration names");
        AssertHit(latch, "FrameViewProjection()", "a render-origin helper call");
        AssertHit(Reads("NullForgiving.cs", "Matrix4x4 view = CameraOverride!.View;"), "CameraOverride.View",
            "a read through the null-forgiving operator");
    }

    static void AssertHit(List<string> hits, string read, string arm)
        => Assert.True(hits.Any(hit => hit.EndsWith("  " + read, StringComparison.Ordinal)),
            $"the matcher missed {read}, {arm}. It found:\n" + string.Join("\n", hits));

    /// <summary>Every camera matrix read and render-origin helper call in one comment-blanked file.</summary>
    internal static List<string> Reads(string path, string text)
    {
        var receivers = new HashSet<string>(WellKnownReceivers, StringComparer.Ordinal);
        foreach (Match declaration in Regex.Matches(text, @"\b(?:" + string.Join("|", CameraTypes) + @")\??\s+(?<name>[A-Za-z_]\w*)"))
            receivers.Add(declaration.Groups["name"].Value);
        string read = @"\b(?<receiver>" + string.Join("|", receivers.Select(Regex.Escape)) + @")\s*[?!]?\.\s*(?<member>"
            + MatrixMembers + @")\b";
        string file = Path.GetFileName(path);
        var hits = new List<string>();
        foreach (Match match in Regex.Matches(text, read))
            hits.Add($"{file}:{SourceSweep.Line(text, match.Index)}  {match.Groups["receiver"].Value}.{match.Groups["member"].Value}");
        foreach (Match match in Regex.Matches(text, HelperCall))
            hits.Add($"{file}:{SourceSweep.Line(text, match.Index)}  {match.Value.TrimEnd('(', ' ', '\t')}()");
        return hits;
    }

    /// <summary>One matrix-carrying call, how many there are, what its arguments must and must not contain, and why.</summary>
    internal readonly record struct Site(string File, string Call, int Count, string Required, string Forbidden, string Why);

    /// <summary>The snapshot as a Scene3D partial names it: the backing field, or the by-value getter.</summary>
    const string Snapshot = @"\b(?:_c|C)urrentFrameView";
    const string RasterForbidden = @"\bdisplayVp\b|" + Snapshot + @"\.ViewProjection\b|" + Snapshot + @"\.Projection\b|ActiveCamera";
    const string DisplayForbidden = @"\bvp\b|Jittered|ActiveCamera";
    const string CpuForbidden = @"Jittered|ActiveCamera\.(?:View|Projection)\b";

    /// <summary>
    /// Every call in the frame that is handed a camera matrix, and the side of the snapshot it takes. Rasterised into
    /// the internal target: jittered. Drawn at display size after post, or applied after the resolve in round 2:
    /// unjittered. CPU spatial work: unjittered. A new matrix-carrying pass adds its row here, and
    /// <see cref="EveryMatrixUseInScene3DHasACallSiteRow"/> fails on a matrix use that has none.
    /// </summary>
    internal static readonly Site[] Sites =
    {
        new("Scene3D.cs", "_model.SetFrameUniforms(", 1, @"\bvp\b", RasterForbidden,
            "the frame block the model, splat, tile ground, foliage and skinned pipelines rasterise with"),
        new("Scene3D.cs", "BuildAndUploadPointLightClusters(", 1, @"\bvp\b", RasterForbidden,
            "the cluster grid a lit fragment indexes through the frame block's jittered ViewProj"),
        new("Scene3D.cs", "_decalRenderer.Draw(", 2, @"\bvp\b", RasterForbidden, "blob shadows and ground decals"),
        new("Scene3D.cs", "DrawOverlayMeshes(", 1, @"\bvp\b", RasterForbidden, "overlay meshes in the model target"),
        new("Scene3D.cs", "DrawSilhouettes(", 1, @"\bvp\b", RasterForbidden, "silhouette hulls in the model target"),
        new("Scene3D.cs", "_sky.Draw(", 1, @"^(?=.*" + Snapshot + @"\.View\b)(?=.*" + Snapshot + @"\.JitteredProjection\b)",
            RasterForbidden, "the sky, which rebuilds NDC from gl_FragCoord through the projection"),
        new("Scene3D.cs", "_water.Draw(", 1, @"\bvp\b", RasterForbidden, "water"),
        new("Scene3D.cs", "_particleRenderer.Draw(", 1, @"\bvp\b", RasterForbidden, "particles"),
        new("Scene3D.cs", "_depthLines.Draw(", 1, @"\bvp\b", RasterForbidden, "depth-tested wire volumes before post"),
        new("Scene3D.cs", "_texBillboards.SetViewProj(", 1, Snapshot + @"\.JitteredViewProjection\b", RasterForbidden,
            "textured billboards in the model target"),
        new("Scene3D.cs", "_beams.SetFrameUniforms(", 1, Snapshot + @"\.JitteredViewProjection\b", RasterForbidden,
            "beams in the model target"),
        new("Scene3D.cs", "_trails.SetFrameUniforms(", 1, Snapshot + @"\.JitteredViewProjection\b", RasterForbidden,
            "trails in the model target"),
        new("Scene3D.cs", "_distortionRenderer.Draw(", 1, @"\bdisplayVp\b", DisplayForbidden,
            "the distortion field, applied after the temporal resolve in round 2"),
        new("Scene3D.cs", "DrawTargetOutlines(", 1, @"\bdisplayVp\b", DisplayForbidden, "target outlines after post"),
        new("Scene3D.cs", "_fills.Draw(", 1, @"\bdisplayVp\b", DisplayForbidden, "filled overlays after post"),
        new("Scene3D.cs", "_lines.Draw(", 1, @"\bdisplayVp\b", DisplayForbidden, "debug lines after post"),
        new("Scene3D.cs", "_billboards.Draw(", 2, @"\bdisplayVp\b", DisplayForbidden, "legacy overlay billboards after post"),
        new("Scene3D.cs", "ExtractCameraDepth(", 1, Snapshot + @"\.Projection\b", CpuForbidden,
            "the edge pass depth convention"),
        new("Scene3D.cs", "FrustumCornersWorld(", 1, Snapshot + @"\.AbsoluteViewProjection\b", CpuForbidden,
            "the cascade fit"),
        new("Scene3D.cs", "FrustumCulling ? FrustumPlanes.Extract(", 1, @"\babsVp\b", CpuForbidden,
            "the camera frustum every CPU cull reads"),
        new("Scene3D.Foliage.cs", "MetresPerPixel(", 1, Snapshot + @"\.Projection\b", CpuForbidden,
            "the foliage pixel scale"),
        new("Scene3D.PointLightClusters.cs", "_model.BuildAndUploadPointLightClusters(", 1,
            @"^(?=.*\brasterViewProjection\b)(?=.*" + Snapshot + @"\.Projection\b)",
            @"\bdisplayVp\b|ActiveCamera\.(?:View|Projection)\b",
            "the cluster grid on the raster matrix, its projection kind from the unjittered one"),
    };

    [Fact]
    public void EveryMatrixCallTakesTheSideOfTheSnapshotItsTargetNeeds()
    {
        var failures = new List<string>();
        foreach (Site site in Sites)
        {
            string text = SourceSweep.Render3DSource(site.File);
            List<(int Line, string Arguments, int Start, int End)> calls = Calls(text, site.Call);
            if (calls.Count != site.Count)
                failures.Add($"{site.File}: expected {site.Count} call(s) of {site.Call} ({site.Why}), found {calls.Count}. "
                    + "A renamed, added or removed call needs its row in Sites.");
            foreach ((int line, string arguments, _, _) in calls)
            {
                if (!Regex.IsMatch(arguments, site.Required, RegexOptions.Singleline))
                    failures.Add($"{site.File}:{line} {site.Call} ({site.Why}) does not take {site.Required}: ({arguments.Trim()})");
                if (Regex.IsMatch(arguments, site.Forbidden, RegexOptions.Singleline))
                    failures.Add($"{site.File}:{line} {site.Call} ({site.Why}) takes the wrong side of the snapshot: ({arguments.Trim()})");
            }
        }
        Assert.True(failures.Count == 0, "Matrix call sites that read the wrong side of the frame view:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void TheFrameLocalsComeFromTheirSideOfTheSnapshot()
    {
        string text = SourceSweep.Render3DSource("Scene3D.cs");
        Assert.Matches(@"Matrix4x4 vp = " + Snapshot + @"\.JitteredViewProjection\b", text);
        Assert.Matches(@"\babsVp = " + Snapshot + @"\.AbsoluteViewProjection\b", text);
        Assert.Matches(@"Matrix4x4 displayVp = " + Snapshot + @"\.ViewProjection\b", text);
        Assert.Single(Regex.Matches(text, @"\bMatrix4x4 vp\b"));
    }

    /// <summary>The frame's three matrix locals, which only Scene3D.cs declares.</summary>
    const string FrameLocal = @"\b(?:vp|displayVp|absVp)\b";
    const string SnapshotMatrix = Snapshot
        + @"\.(?:View|Projection|ViewProjection|AbsoluteViewProjection|JitteredProjection|JitteredViewProjection)\b";

    /// <summary>
    /// A frame local assigned from its own side of the snapshot, the pairing
    /// <see cref="TheFrameLocalsComeFromTheirSideOfTheSnapshot"/> pins. That local and that read are the declaration,
    /// not a use. A local assigned from any other side matches nothing here, so both of its halves still need a row.
    /// </summary>
    const string FrameLocalDeclarator = @"(?<local>\bvp)\s*=\s*(?<source>" + Snapshot + @"\.JitteredViewProjection\b)"
        + @"|(?<local>\babsVp)\s*=\s*(?<source>" + Snapshot + @"\.AbsoluteViewProjection\b)"
        + @"|(?<local>\bdisplayVp)\s*=\s*(?<source>" + Snapshot + @"\.ViewProjection\b)";

    [Fact]
    public void EveryMatrixUseInScene3DHasACallSiteRow()
    {
        // What the Sites rows cover, per file: each call's argument span and the line its name sits on.
        var covered = new Dictionary<string, List<(int Line, int Start, int End)>>(StringComparer.Ordinal);
        foreach (Site site in Sites)
        {
            if (!covered.TryGetValue(site.File, out List<(int Line, int Start, int End)>? spans))
                covered[site.File] = spans = new List<(int Line, int Start, int End)>();
            foreach ((int line, _, int start, int end) in Calls(SourceSweep.Render3DSource(site.File), site.Call))
                spans.Add((line, start, end));
        }

        string root = SourceSweep.Render3DRoot();
        var uses = new List<string>();
        var uncovered = new List<string>();
        foreach ((string path, string text) in SourceSweep.Render3DSources())
        {
            string file = string.Join("/", SourceSweep.Segments(root, path));
            if (file == "Scene3D.FrameView.cs") continue;   // the latch fills the snapshot
            var matches = Regex.Matches(text, SnapshotMatrix).ToList();
            var declarators = new HashSet<int>();
            if (file == "Scene3D.cs")
            {
                matches.AddRange(Regex.Matches(text, FrameLocal));
                foreach (Match declarator in Regex.Matches(text, FrameLocalDeclarator))
                {
                    declarators.Add(declarator.Groups["local"].Index);
                    declarators.Add(declarator.Groups["source"].Index);
                }
            }
            covered.TryGetValue(file, out List<(int Line, int Start, int End)>? rows);
            foreach (Match match in matches)
            {
                if (declarators.Contains(match.Index)) continue;
                int line = SourceSweep.Line(text, match.Index);
                string use = $"{file}:{line}  {match.Value}";
                uses.Add(use);
                bool inRow = rows is not null && rows.Any(row => row.Line == line
                    || (match.Index >= row.Start && match.Index + match.Length <= row.End));
                if (!inRow) uncovered.Add(use);
            }
        }

        foreach (string use in uses) _output.WriteLine(use);
        Assert.True(uses.Count > 0, "the fact found no matrix use at all, so its matchers are dead rather than every use covered");
        Assert.True(uncovered.Count == 0,
            "These hand a frame matrix to something no Sites row names, so nothing checks which side of the snapshot "
            + "it takes. Add a row for the call that receives each one:\n" + string.Join("\n", uncovered));
    }

    /// <summary>Every call of <paramref name="call"/> in a comment-blanked file: the line its name sits on, its
    /// arguments, and their span as indexes into <paramref name="text"/> with the end exclusive.</summary>
    static List<(int Line, string Arguments, int Start, int End)> Calls(string text, string call)
    {
        var calls = new List<(int, string, int, int)>();
        foreach (Match match in Regex.Matches(text, @"(?<!\w)" + Regex.Escape(call)))
        {
            int open = match.Index + match.Length - 1;   // every Call ends in its opening bracket
            int depth = 0, close = -1;
            for (int i = open; i < text.Length && close < 0; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) close = i;
            }
            int end = close < 0 ? open + 1 : close;
            calls.Add((SourceSweep.Line(text, match.Index), text.Substring(open + 1, end - open - 1), open + 1, end));
        }
        return calls;
    }
}
