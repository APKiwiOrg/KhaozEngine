using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeLegacySpawnFenceTests
{
    [Fact]
    public void NativeLegacySpawnWrites_RefuseAcrossEveryEntry()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var session = MarkerEditorFixtures.Session(fixture);
        var mutation = new MutationService(session);
        string before = MarkerEditorFixtures.State(session);
        foreach (Action attempt in ServiceAttempts(mutation))
        {
            Guidance(Assert.Throws<MapDocumentException>(attempt));
            Assert.Equal(before, MarkerEditorFixtures.State(session));
        }
        var gui = fixture.Document;
        string guiBefore = MapDocumentFile.SaveText(gui);
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        foreach (var command in LegacyCommands())
        {
            Guidance(Assert.Throws<MapDocumentException>(() => history.Execute(gui, command)));
            Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
            Assert.False(history.CanUndo);
        }
        foreach (bool player in new[] { false, true })
        {
            Guidance(Assert.Throws<MapDocumentException>(() => session.Mutate((doc, _) => { Add(doc, player); return 0; }, false)));
            Assert.Equal(before, MarkerEditorFixtures.State(session));
            var after = MapDocumentFile.LoadText(guiBefore);
            Add(after, player);
            var snapshot = new NativeCompleteSnapshotCommand("snapshot", gui, after, fixture.Assets, fixture.Assets, new(null, null, [], [], [], [], MapNativeInvalidation.None));
            Guidance(Assert.Throws<MapDocumentException>(() => history.Execute(gui, snapshot)));
            Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
        }
        Guidance(Assert.Throws<MapDocumentException>(() => history.Execute(gui, new ForgedLegacyWriteCommand())));
        Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
    }

    [Theory]
    [InlineData(false, "npc-add")]
    [InlineData(false, "player-add")]
    [InlineData(false, "npc-move")]
    [InlineData(false, "player-move")]
    [InlineData(false, "replace-list")]
    [InlineData(true, "npc-add")]
    [InlineData(true, "player-add")]
    [InlineData(true, "replace-list")]
    public void LegacyListsChangedAfterStaging_RefuseAtPublication(bool adoption, string change)
    {
        var session = MarkerEditorFixtures.Session(MarkerFixtures.LegacyLists());
        string stateBefore = MarkerEditorFixtures.State(session);
        int hooks = 0;
        session.BeforeNativePublication = () => hooks++;
        var lateRecord = Assert.Throws<MapDocumentException>(() => session.Mutate((doc, registry) =>
        {
            session.ApplyNative(Command(adoption), doc, registry);
            switch (change)
            {
                case "npc-add": Add(doc, false); break;
                case "player-add": Add(doc, true); break;
                case "npc-move": doc.Spawns[0].X += 1; break;
                case "player-move": doc.PlayerSpawns[0].X += 1; break;
                default: doc.Spawns = [new() { Id = "late", ArchetypeId = "kit.actor", X = 1, Z = 1 }]; break;
            }
            return 0;
        }, true));
        Assert.Contains("spawns_adopt", lateRecord.Message, StringComparison.Ordinal);
        Assert.Equal(stateBefore, MarkerEditorFixtures.State(session));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedStagedCommands_AndAdoptionHistoryPublish(bool adoption)
    {
        var session = MarkerEditorFixtures.Session(MarkerFixtures.LegacyLists());
        string sourceListBytes = NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session));
        session.Mutate((doc, registry) => { session.ApplyNative(Command(adoption), doc, registry); return 0; }, true);
        string adoptedListBytes = NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session));
        Assert.NotNull(session.UndoNative());
        var afterUndo = PrefabEditorFixtures.Document(session);
        Assert.Equal(sourceListBytes, NativeLegacySpawnAdmission.Canonical(afterUndo));
        Assert.NotNull(session.RedoNative());
        var afterRedo = PrefabEditorFixtures.Document(session);
        Assert.Equal(adoptedListBytes, NativeLegacySpawnAdmission.Canonical(afterRedo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransactionPublication_RechecksCapturedPermitBeforeAnyWrite(bool adoption)
    {
        var fixture = MarkerFixtures.LegacyLists();
        var history = PrefabEditorFixtures.History(fixture.Document, fixture.Assets);
        string before = MapDocumentFile.SaveText(fixture.Document);
        var staged = history.StageExecute(fixture.Document, Command(adoption));
        Add(staged.Candidate, false);
        Guidance(Assert.Throws<MapDocumentException>(() => NativeDocumentTransaction.Publish(fixture.Document, staged)));
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ReusedAdoption_DoesNotIssuePermitForDifferentLiveLists()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var history = PrefabEditorFixtures.History(fixture.Document, fixture.Assets);
        var command = new AdoptSpawnsCommand(MarkerFixtures.Choices());
        history.Execute(fixture.Document, command);
        var staged = NativeDocumentTransaction.Stage(fixture.Document, command, false, fixture.Assets);
        Assert.Null(staged.LegacySpawns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericLegacyListEdits_RefuseBeforeValidation(bool player)
    {
        var session = MarkerEditorFixtures.Session(MarkerFixtures.LegacyLists());
        string before = MarkerEditorFixtures.State(session);
        int hooks = 0;
        session.BeforeNativePublication = () => hooks++;
        var error = Assert.Throws<MapDocumentException>(() => session.Mutate((doc, _) =>
        {
            if (player) doc.PlayerSpawns.Add(new() { Id = "old", X = 2, Z = -2 });
            else doc.Spawns.Add(new() { Id = "old", ArchetypeId = "kit.actor", X = 2, Z = -2 });
            return 0;
        }, false));
        Guidance(error);
        Assert.Equal(before, MarkerEditorFixtures.State(session));
        Assert.Equal(0, hooks);
    }

    [Fact]
    public void OldNativeLists_LoadSaveAndUnrelatedMarkerEditsRemainAvailable()
    {
        var fixture = MarkerFixtures.Reload(MarkerFixtures.LegacyLists(), false);
        var session = MarkerEditorFixtures.Session(fixture);
        string old = NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session));
        new MutationService(session).MarkerApply(new MapSetMarker(MarkerFixtures.BasicMarker()));
        Assert.Equal(old, NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(session)));
        Assert.True(session.Save().Saved);
        var reopened = new MapEditSession();
        reopened.Open(session.DocumentPath!);
        Assert.Equal(old, NativeLegacySpawnAdmission.Canonical(PrefabEditorFixtures.Document(reopened)));
    }

    [Fact]
    public void ResolverOneNative_AdoptionAndSpawnVerbsNameResolverAdoption()
    {
        var fixture = FormatFourFixtures.Build();
        var session = PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));
        // The shared marker snapshot resolves with resolver 2. This fixture deliberately keeps resolver 1.
        string State() => session.WithNativeAssets(c => string.Join("\n", MapDocumentFile.SaveText(c.Document),
            MapDocumentHash.OfWorld(c.Document, MapDocRegistry.CreateDefault()), c.Assets.Hash, session.NativeHistoryState,
            session.NativeHistoryLabels, session.LastNativeEffects?.Describe(), session.IsDirty));
        string before = State();
        foreach (Action attempt in new Action[]
        {
            () => new MutationService(session).SpawnsAdopt(MarkerFixtures.Choices()),
            () => new MutationService(session).SpawnAdd("kit.actor", 1, 1),
        })
        {
            var error = Assert.Throws<MapDocumentException>(attempt);
            Assert.Contains("resolver adoption", error.Message, StringComparison.Ordinal);
            Assert.Contains("MapResolverAdoption.ConvertToAuthoredSupport", error.Message, StringComparison.Ordinal);
            Assert.Equal(before, State());
        }
    }

    [Fact]
    public void AnalyticSpawnVerbs_KeepReleasedBehavior()
    {
        var session = new MapEditSession();
        session.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "legacy-" + Guid.NewGuid().ToString("N"), "map.json"), "legacy-analytic", "Analytic", -32, -32, 32, 32);
        var mutation = new MutationService(session);
        mutation.SpawnAdd("kit.actor", 1, 2, id: "npc");
        mutation.SpawnMove("npc", 2, 3);
        mutation.SpawnRename("npc", "npc-new");
        mutation.SpawnSetEnabled("npc-new", false);
        mutation.PlayerSpawnAdd(1, 2, id: "start");
        mutation.PlayerSpawnMove("start", 2, 3);
        mutation.PlayerSpawnRename("start", "start-new");
        mutation.PlayerSpawnSetEnabled("start-new", false);
        mutation.PlayerSpawnSetYaw("start-new", 1.25f);
        session.WithDocument((doc, _) =>
        {
            var npc = Assert.Single(doc.Spawns);
            var player = Assert.Single(doc.PlayerSpawns);
            Assert.Equal(("npc-new", 2f, 3f, false), (npc.Id, npc.X, npc.Z, npc.Enabled));
            Assert.Equal(("start-new", 2f, 3f, false, 1.25f), (player.Id, player.X, player.Z, player.Enabled, player.Yaw));
            return 0;
        });
        mutation.SpawnRemove("npc-new");
        mutation.PlayerSpawnRemove("start-new");
        session.WithDocument((doc, _) => { Assert.Empty(doc.Spawns); Assert.Empty(doc.PlayerSpawns); return 0; });
        var gui = MapDocumentFile.LoadText(MarkerFixtures.FormatSevenJson(true));
        var history = new EditorHistory();
        history.Execute(gui, new AddSpawnCommand(new() { Id = "npc", ArchetypeId = "kit.actor", X = 1, Z = 2 }));
        history.Execute(gui, new AddPlayerSpawnCommand(new() { Id = "start", X = 1, Z = 2, Yaw = 0.371f }));
        history.Execute(gui, new RemoveSpawnCommand("npc"));
        Assert.True(history.Undo(gui));
        Assert.Equal("npc", Assert.Single(gui.Spawns).Id);
        Assert.True(history.Redo(gui));
        Assert.Empty(gui.Spawns);
        history.Execute(gui, new RemovePlayerSpawnCommand("start"));
        Assert.True(history.Undo(gui));
        Assert.Equal(0.371f, Assert.Single(gui.PlayerSpawns).Yaw);
        Assert.True(history.Redo(gui));
        Assert.Empty(gui.PlayerSpawns);
    }

    static EditorCommand Command(bool adoption) => adoption ? new AdoptSpawnsCommand(MarkerFixtures.Choices()) :
        new MarkerEditCommand(new MapSetMarker(MapMarkerHeights.Apply(MarkerFixtures.BasicMarker() with { Id = "probe", Name = "probe" }, new(1, null))));

    static void Add(MapDocument document, bool player)
    {
        if (player) document.PlayerSpawns.Add(new() { Id = "late", X = 2, Z = -2 });
        else document.Spawns.Add(new() { Id = "late", ArchetypeId = "kit.actor", X = 2, Z = -2 });
    }

    static void Guidance(MapDocumentException error)
    {
        Assert.Contains("marker_set", error.Message, StringComparison.Ordinal);
        Assert.Contains("marker_remove", error.Message, StringComparison.Ordinal);
        Assert.Contains("spawns_adopt", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("resolver adoption", error.Message, StringComparison.Ordinal);
    }

    static IEnumerable<Action> ServiceAttempts(MutationService mutation) => new Action[]
    {
        () => mutation.SpawnAdd("kit.actor", 1, 1), () => mutation.SpawnMove("old", 1, 1),
        () => mutation.SpawnRename("old", "new"), () => mutation.SpawnSetEnabled("old", true), () => mutation.SpawnRemove("old"),
        () => mutation.PlayerSpawnAdd(1, 1), () => mutation.PlayerSpawnMove("old", 1, 1), () => mutation.PlayerSpawnRename("old", "new"),
        () => mutation.PlayerSpawnSetEnabled("old", false), () => mutation.PlayerSpawnSetYaw("old", 1), () => mutation.PlayerSpawnRemove("old"),
        () => mutation.ElementDuplicate("spawn", "old"), () => mutation.ElementDuplicate("player_spawn", "old"),
    };

    static EditorCommand[] LegacyCommands() =>
    [
        new AddSpawnCommand(new() { Id = "new", ArchetypeId = "kit.actor", X = 1, Z = 1 }), new MoveSpawnCommand("old", 1, 1),
        new RenameSpawnCommand("old", "new"), new SetSpawnEnabledCommand("old", true), new RemoveSpawnCommand("old"),
        new AddPlayerSpawnCommand(new() { Id = "new", X = 1, Z = 1 }), new MovePlayerSpawnCommand("old", 1, 1),
        new RenamePlayerSpawnCommand("old", "new"), new SetPlayerSpawnEnabledCommand("old", false),
        new SetPlayerSpawnYawCommand("old", 1), new RemovePlayerSpawnCommand("old"),
    ];

    sealed class ForgedLegacyWriteCommand : EditorCommand, INativeBoundDocumentCommand
    {
        public override string Label => "forged legacy writes";
        internal override bool AffectsWorld => false;
        public override void Apply(MapDocument doc) => throw new NotSupportedException();
        public override void Revert(MapDocument doc) => throw new NotSupportedException();
        NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets)
        {
            Add(candidate, false);
            return new(new([], [], [], [], [], [], false) { LegacySpawns = true }, new(null, null, [], [], [], [], MapNativeInvalidation.None), () => { });
        }
    }
}
