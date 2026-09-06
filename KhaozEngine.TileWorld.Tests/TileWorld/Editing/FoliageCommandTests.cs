using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless command tests for cosmetic foliage layers. They pin replacement, removal, undo and redo,
/// dirty coverage for arbitrary tile sizes, and the stronger rule that foliage edits never change collision.</summary>
public class FoliageCommandTests
{
    static TileFoliageLayer Layer(string id = "meadow", int plane = 0, float originX = -2.5f,
        float originZ = -7.5f, byte[]? density = null) => new(
        id, plane, originX, originZ, 0.5f, 3, 2, density ?? new byte[] { 0, 64, 128, 192, 224, 255 },
        seed: 7, spacing: 0.3f, scaleMin: 0.8f, scaleMax: 1.2f, rootOffset: -0.04f,
        archetypes: new[] { new TileFoliageArchetype("grass-a", 2f) }, allowedUnderlays: new ushort[] { 1, 2 });

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        doc.TileSize = 2.5f;
        return new TileEditingDocument(doc, TileWorldCatalogs.Greybox());
    }

    [Fact]
    public void Set_replaces_and_undo_restores_the_whole_immutable_layer()
    {
        TileEditingDocument editing = Editing(out TileWorldDocument doc);
        TileFoliageLayer first = Layer(density: new byte[] { 1, 2, 3, 4, 5, 6 });
        doc.SetFoliageLayer(first);
        TileCollisionFlags before = editing.Collision.Get(0, 0, 0);
        TileFoliageLayer replacement = Layer(density: new byte[] { 6, 5, 4, 3, 2, 1 });

        var command = new SetFoliageLayerCommand(doc, replacement);
        editing.Execute(command);

        Assert.Equal("Set foliage layer", command.Label);
        Assert.Same(replacement, doc.GetFoliageLayer("meadow"));
        Assert.Single(command.DirtyRects);
        Assert.Equal(new TileDirtyRect(new TileRect(-1, 2, 1, 2), 0), command.DirtyRects.Single());
        Assert.Equal(before, editing.Collision.Get(0, 0, 0));

        Assert.True(editing.Undo());
        Assert.Same(first, doc.GetFoliageLayer("meadow"));
        Assert.Equal(before, editing.Collision.Get(0, 0, 0));
        Assert.True(editing.Redo());
        Assert.Same(replacement, doc.GetFoliageLayer("meadow"));
    }

    [Fact]
    public void Set_of_a_new_layer_undoes_to_absent()
    {
        TileEditingDocument editing = Editing(out TileWorldDocument doc);

        editing.Execute(new SetFoliageLayerCommand(doc, Layer()));
        Assert.NotNull(doc.GetFoliageLayer("meadow"));

        Assert.True(editing.Undo());
        Assert.Null(doc.GetFoliageLayer("meadow"));
    }

    [Fact]
    public void Remove_round_trips_and_missing_is_rejected_before_history_changes()
    {
        TileEditingDocument editing = Editing(out TileWorldDocument doc);
        TileFoliageLayer layer = Layer();
        doc.SetFoliageLayer(layer);

        editing.Execute(new RemoveFoliageLayerCommand(doc, "meadow"));
        Assert.Null(doc.GetFoliageLayer("meadow"));
        Assert.True(editing.Undo());
        Assert.Same(layer, doc.GetFoliageLayer("meadow"));

        int depth = editing.History.UndoDepth;
        Assert.Throws<TileWorldException>(() => new RemoveFoliageLayerCommand(doc, "missing"));
        Assert.Equal(depth, editing.History.UndoDepth);
        Assert.Same(layer, doc.GetFoliageLayer("meadow"));
    }

    [Fact]
    public void Replacement_dirty_rect_covers_both_old_and_new_extents_and_planes()
    {
        TileEditingDocument editing = Editing(out TileWorldDocument doc);
        doc.SetFoliageLayer(Layer(plane: 0));
        TileFoliageLayer moved = Layer(plane: 1, originX: 100f, originZ: 100f);

        var command = new SetFoliageLayerCommand(doc, moved);
        editing.Execute(command);

        Assert.Equal(2, command.DirtyRects.Count());
        Assert.Contains(command.DirtyRects, d => d.Plane == 0 && d.Rect.Contains(-1, 3));
        Assert.Contains(command.DirtyRects, d => d.Plane == 1 && d.Rect.Contains(40, -40));
    }
}
