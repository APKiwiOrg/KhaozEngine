using System;
using System.IO;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Headless tests for the ke-tileedit session: create, open, save, validate, the summary, the dirty
/// flag, undo and redo, and the rule that catalog paths resolve against the WORLD directory rather than the
/// process working directory.</summary>
public class TileEditSessionTests
{
    [Fact]
    public void Create_WritesTheWorldAndKeepsItOpen()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileEditTestWorld.WriteCatalog(Path.Combine(dir, TileEditTestWorld.CatalogFileName));

        var session = new TileEditSession();
        OpenResult result = session.Create(dir, "grimhollow", "Grimhollow", 4, 1f,
            new[] { TileEditTestWorld.CatalogFileName });

        Assert.Equal(dir, result.Path);
        Assert.Equal("grimhollow", result.Id);
        Assert.Equal("Grimhollow", result.DisplayName);
        Assert.True(File.Exists(TileWorldFile.ManifestPath(dir)), "world_create wrote no manifest.");

        WorldSummary summary = result.Summary;
        Assert.Equal(4, summary.PlaneCount);
        Assert.Equal(1f, summary.TileSize);
        Assert.Equal(1, summary.RegionCount);
        Assert.Equal(0, summary.ObjectCount);
        Assert.Equal(0, summary.MarkerCount);
        Assert.False(summary.Dirty);
        Assert.Equal(0, summary.UndoDepth);
        Assert.Null(summary.UndoLabel);
        // The MANIFEST keeps the entry exactly as given, so the world stays portable.
        Assert.Equal(new[] { TileEditTestWorld.CatalogFileName }, summary.CatalogPaths);
        Assert.True(session.HasDocument);
        Assert.Equal(dir, session.DocumentPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, TileEditTestWorld.CatalogFileName)), session.CatalogPaths[0]);
    }

    [Fact]
    public void Create_RefusesADirectoryThatAlreadyHoldsAWorld()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileEditSession session = TileEditTestWorld.NewSession(dir);

        TileWorldException ex = Assert.Throws<TileWorldException>(() =>
            session.Create(dir, "other", "Other", 4, 1f, new[] { TileEditTestWorld.CatalogFileName }));

        Assert.Contains("already exists", ex.Message);
        // The refusal must not have disturbed the world that was open.
        Assert.Equal("test-world", session.Summary().Id);
    }

    /// <summary>The catalog sits BESIDE the world directory and is named <c>../greybox.json</c>, and it exists
    /// nowhere else. So an open that succeeds can only have resolved the entry against the world's own
    /// directory: no working directory is set, changed, or relied on anywhere in this test.</summary>
    [Fact]
    public void Open_ResolvesRelativeCatalogPathsAgainstTheWorldDirectory()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileEditTestWorld.WriteCatalog(tmp.Sub(TileEditTestWorld.CatalogFileName));
        new TileEditSession().Create(dir, "sibling", "Sibling", 2, 1f,
            new[] { "../" + TileEditTestWorld.CatalogFileName });

        var reopened = new TileEditSession();
        reopened.Open(dir);

        Assert.Equal("sibling", reopened.Summary().Id);
        Assert.Equal(Path.GetFullPath(tmp.Sub(TileEditTestWorld.CatalogFileName)), reopened.CatalogPaths[0]);
        Assert.NotEmpty(new QueryService(reopened).CatalogList("materials").Materials);
    }

    [Fact]
    public void Open_NormalisesTheWorldPathItEchoesBack()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileEditTestWorld.NewSession(dir);

        var reopened = new TileEditSession();
        // The same directory reached the long way round. Every other path this tool returns is normalised
        // through ResolvePath, so the world's own must be too rather than echoing the client's spelling.
        OpenResult opened = reopened.Open(Path.Combine(dir, "..", "world"));

        Assert.Equal(Path.GetFullPath(dir), opened.Path);
        Assert.DoesNotContain("..", reopened.Summary().Path, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(dir), reopened.Save().Path);
    }

    [Fact]
    public void Execute_MarksDirtyAndReportsTheTouchedRects()
    {
        using var tmp = new TempDir();
        TileEditSession session = TileEditTestWorld.NewSession(tmp.Sub("world"));
        string before = session.Summary().WorldHash;

        MutationResult result = session.Execute(_ =>
            new SetTilesCommand(new TileRect(2, 3, 4, 5), 0, 1, null, null, null, null));

        Assert.Equal("Set tiles", result.Label);
        Assert.True(result.Dirty);
        Assert.Equal(1, result.UndoDepth);
        Assert.NotEqual(before, result.WorldHash);
        DirtyRectInfo rect = Assert.Single(result.DirtyRects);
        Assert.Equal(new RectInfo(2, 3, 4, 5), rect.Rect);
        Assert.Equal(0, rect.Plane);
        // Acknowledged as they are handed out, so the next edit reports only its own.
        Assert.Empty(session.Editing!.PendingRebuilds);
    }

    [Fact]
    public void Save_ValidatesClearsDirtyAndSurvivesAReopen()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileEditSession session = TileEditTestWorld.NewSession(dir);
        var mutate = new MutationService(session);
        mutate.TilesFill(new TileRect(0, 0, 3, 3), 0, underlay: 2);
        Assert.True(session.Summary().Dirty);

        SaveResult saved = session.Save();

        Assert.Equal(dir, saved.Path);
        Assert.False(session.Summary().Dirty);
        Assert.Equal(session.Summary().WorldHash, saved.WorldHash);

        var reopened = new TileEditSession();
        reopened.Open(dir);
        Assert.Equal((ushort)2, new QueryService(reopened).TileGet(1, 1, 0).Underlay);
        Assert.Equal(saved.WorldHash, reopened.Summary().WorldHash);
    }

    [Fact]
    public void UndoAndRedo_ReportTheStepsThatMovedAndTheLabels()
    {
        using var tmp = new TempDir();
        TileEditSession session = TileEditTestWorld.NewSession(tmp.Sub("world"));
        var mutate = new MutationService(session);
        mutate.TilesFill(new TileRect(0, 0, 2, 2), 0, underlay: 1);
        session.SealGesture();
        mutate.ObjectPlace("tree", 5, 5, 0);
        string hashWithTree = session.Summary().WorldHash;

        UndoResult undone = session.Undo(5);

        // Two steps on the stack, five asked for: it reports what actually moved.
        Assert.Equal(2, undone.Steps);
        Assert.Equal(0, undone.UndoDepth);
        Assert.Equal(2, undone.RedoDepth);
        Assert.Null(undone.UndoLabel);
        Assert.Equal("Set tiles", undone.RedoLabel);
        Assert.False(undone.Dirty);

        UndoResult redone = session.Redo(2);

        Assert.Equal(2, redone.Steps);
        Assert.Equal("Place object", redone.UndoLabel);
        Assert.Null(redone.RedoLabel);
        Assert.Equal(hashWithTree, redone.WorldHash);
        Assert.Equal(0, session.Redo(1).Steps);
    }

    [Fact]
    public void EveryMemberOnAClosedSession_ThrowsNamingTheOpeningVerbs()
    {
        var session = new TileEditSession();

        Assert.False(session.HasDocument);
        Assert.Null(session.DocumentPath);
        Assert.Null(session.Editing);
        foreach (System.Action call in new System.Action[]
        {
            () => session.RequireOpen(),
            () => session.Summary(),
            () => session.Save(),
            () => session.Validate(),
            () => session.Read(e => e.Document.Id),
            () => session.Execute(_ => new CreateRegionCommand(new RegionCoord(0, 0))),
            () => session.Undo(),
            () => session.Redo(),
            () => session.SealGesture(),
        })
        {
            TileWorldException ex = Assert.Throws<TileWorldException>(call);
            Assert.Contains("world_open", ex.Message);
            Assert.Contains("world_create", ex.Message);
        }
    }

    [Fact]
    public void Validate_ReportsTheIssuesAndSaveRefusesTheWorld()
    {
        using var tmp = new TempDir();
        TileEditSession session = TileEditTestWorld.NewSession(tmp.Sub("world"));
        Assert.True(session.Validate().Valid);

        // Material 99 is in no catalog, which the validator reports and the save refuses.
        new MutationService(session).TilesFill(new TileRect(0, 0, 1, 1), 0, underlay: 99);

        ValidateResult validated = session.Validate();
        Assert.False(validated.Valid);
        Assert.Contains(validated.Issues, i => i.StartsWith("[material.missing]", System.StringComparison.Ordinal));
        Assert.Throws<TileWorldException>(() => session.Save());
    }

    [Fact]
    public void Summary_CountsRegionsObjectsAndMarkers()
    {
        using var tmp = new TempDir();
        TileEditSession session = TileEditTestWorld.NewSession(tmp.Sub("world"));
        var mutate = new MutationService(session);
        TileEditTestWorld.Build(mutate);
        mutate.RegionCreate(1, 0);

        WorldSummary summary = session.Summary();

        Assert.Equal(2, summary.RegionCount);
        Assert.Equal(2, summary.ObjectCount);
        Assert.Equal(1, summary.MarkerCount);
        Assert.True(summary.Dirty);
        Assert.Equal("Create region", summary.UndoLabel);
        Assert.Equal(session.Editing!.History.UndoDepth, summary.UndoDepth);
        Assert.Equal(2, new QueryService(session).RegionList().Count);
    }
}
