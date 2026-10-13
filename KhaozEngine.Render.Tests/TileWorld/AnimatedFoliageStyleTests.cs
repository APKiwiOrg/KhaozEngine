using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class AnimatedFoliageStyleTests
{
    static readonly AnimatedFoliageStyle Shrub = new(new FoliageMotionProfile(.3f, .15f, .4f, .035f), true);

    static AnimatedFoliageStyleSnapshot CreateSnapshot(IReadOnlySet<string> selection,
        IReadOnlyDictionary<string, AnimatedFoliageStyle> styles, TileWorldCatalogs catalogs,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes) => new(selection, styles, catalogs, meshes);

    static TileWorldCatalogs SharedCatalogs() => TileWorldCatalogs.Merge(TileRenderTestData.Catalogs,
        TileWorldCatalogs.LoadJson("""
            { "archetypes": [
              { "id": "bush_alias", "name": "bush alias", "meshRef": "greybox/bush.glb" }
            ] }
            """, "foliage styles"));

    [Fact]
    public void StyleOutsideSelectionNamesTheId()
    {
        var selection = new HashSet<string>(StringComparer.Ordinal) { "bush" };
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["tree"] = Shrub };

        ArgumentException outside = Assert.Throws<ArgumentException>(() =>
            CreateSnapshot(selection, styles, TileRenderTestData.Catalogs, new Dictionary<string, IReadOnlyList<MeshHandle>>()));

        Assert.Equal("AnimatedFoliageStyles", outside.ParamName);
        Assert.Contains("tree", outside.Message);
    }

    [Fact]
    public void SharedMeshComparesImplicitBladeAndExplicitStyles()
    {
        var sharedSelection = new HashSet<string>(StringComparer.Ordinal) { "bush_alias", "bush" };
        var shrubOnly = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["bush"] = Shrub };
        TileWorldCatalogs catalogs = SharedCatalogs();
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>();

        ArgumentException conflict = Assert.Throws<ArgumentException>(() =>
            CreateSnapshot(sharedSelection, shrubOnly, catalogs, meshes));

        Assert.Equal("AnimatedFoliageStyles", conflict.ParamName);
        Assert.Contains("bush_alias", conflict.Message);
        Assert.Contains("bush", conflict.Message);
        Assert.True(conflict.Message.IndexOf("'bush'", StringComparison.Ordinal) <
                    conflict.Message.IndexOf("'bush_alias'", StringComparison.Ordinal));
        shrubOnly["bush"] = new AnimatedFoliageStyle(FoliageMotionProfile.Blade, false);
        AnimatedFoliageStyleSnapshot compatible = CreateSnapshot(sharedSelection, shrubOnly, catalogs, meshes);
        Assert.Equal(FoliageMotionProfile.Blade, compatible.MotionProfiles["bush"]);
        Assert.False(compatible.ModelCastsShadows["bush_alias"]);
    }

    [Fact]
    public void DifferentMeshRefsCompareEveryLoadedPartHandle()
    {
        TileWorldCatalogs catalogs = SharedCatalogs();
        catalogs.Archetypes["bush_alias"].MeshRef = "other/bush.glb";
        var selection = new HashSet<string>(StringComparer.Ordinal) { "bush_alias", "bush" };
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["bush"] = Shrub };
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal)
        {
            ["bush"] = [new MeshHandle(1), new MeshHandle(3)],
            ["bush_alias"] = [new MeshHandle(2), new MeshHandle(3)],
        };

        ArgumentException conflict = Assert.Throws<ArgumentException>(() => CreateSnapshot(selection, styles, catalogs, meshes));

        Assert.Contains("bush_alias", conflict.Message);
        Assert.Contains("bush", conflict.Message);
        Assert.True(conflict.Message.IndexOf("'bush'", StringComparison.Ordinal) <
                    conflict.Message.IndexOf("'bush_alias'", StringComparison.Ordinal));
        styles["bush_alias"] = Shrub;
        AnimatedFoliageStyleSnapshot compatible = CreateSnapshot(selection, styles, catalogs, meshes);
        Assert.Equal(Shrub.Motion, compatible.MotionProfiles["bush_alias"]);
        Assert.True(compatible.ModelCastsShadows["bush"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedMeshesRejectCastingOnlyConflicts(bool sharedHandle)
    {
        TileWorldCatalogs catalogs = SharedCatalogs();
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal);
        if (sharedHandle)
        {
            catalogs.Archetypes["bush_alias"].MeshRef = "other/bush.glb";
            meshes["bush"] = [new MeshHandle(7)];
            meshes["bush_alias"] = [new MeshHandle(7)];
        }
        var selection = new HashSet<string>(StringComparer.Ordinal) { "bush", "bush_alias" };
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal)
        {
            ["bush"] = new(FoliageMotionProfile.Blade, true),
        };

        ArgumentException conflict = Assert.Throws<ArgumentException>(() => CreateSnapshot(selection, styles, catalogs, meshes));

        Assert.Equal("AnimatedFoliageStyles", conflict.ParamName);
        Assert.Contains("bush", conflict.Message);
        Assert.Contains("bush_alias", conflict.Message);
    }

    [Fact]
    public void EmptyAndCaseDistinctMeshRefsDoNotAliasDifferentHandles()
    {
        TileWorldCatalogs catalogs = SharedCatalogs();
        var selection = new HashSet<string>(StringComparer.Ordinal) { "bush", "bush_alias" };
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["bush"] = Shrub };
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal)
        {
            ["bush"] = [new MeshHandle(7, 1)],
            ["bush_alias"] = [new MeshHandle(7, 2)],
        };
        catalogs.Archetypes["bush_alias"].MeshRef = "greybox/BUSH.glb";

        Assert.Equal(Shrub.Motion, CreateSnapshot(selection, styles, catalogs, meshes).MotionProfiles["bush"]);
        catalogs.Archetypes["bush"].MeshRef = "";
        catalogs.Archetypes["bush_alias"].MeshRef = "";
        Assert.False(CreateSnapshot(selection, styles, catalogs, meshes).ModelCastsShadows["bush_alias"]);
    }

    [Fact]
    public void SnapshotDetachesStylesAndDetectsMutationsAndRemovals()
    {
        var selection = new HashSet<string>(StringComparer.Ordinal) { "bush" };
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["bush"] = Shrub };
        AnimatedFoliageStyleSnapshot snapshot = CreateSnapshot(selection, styles, TileRenderTestData.Catalogs,
            new Dictionary<string, IReadOnlyList<MeshHandle>>());

        Assert.True(snapshot.Matches(new HashSet<string>(selection, StringComparer.Ordinal),
            new Dictionary<string, AnimatedFoliageStyle>(styles, StringComparer.Ordinal)));
        styles["bush"] = new(FoliageMotionProfile.Blade, false);
        Assert.False(snapshot.Matches(selection, styles));
        Assert.Equal(Shrub.Motion, snapshot.MotionProfiles["bush"]);
        Assert.True(snapshot.ModelCastsShadows["bush"]);
        styles["bush"] = Shrub;
        styles.Remove("bush");
        Assert.False(snapshot.Matches(selection, styles));
        styles["bush"] = Shrub;
        selection.Add("tree");
        Assert.False(snapshot.Matches(selection, styles));
        Assert.False(snapshot.MotionProfiles.ContainsKey("tree"));
    }

    [Fact]
    public void EmptySelectionStillRejectsEveryStyle()
    {
        ArgumentException outside = Assert.Throws<ArgumentException>(() => CreateSnapshot(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal) { ["tree"] = Shrub },
            TileRenderTestData.Catalogs, new Dictionary<string, IReadOnlyList<MeshHandle>>()));

        Assert.Equal("AnimatedFoliageStyles", outside.ParamName);
        Assert.Contains("tree", outside.Message);
    }

    [Fact]
    public void NullMapsAndSelectionAreRejected()
    {
        var selection = new HashSet<string>(StringComparer.Ordinal);
        var styles = new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;

        Assert.Equal("styles", Assert.Throws<ArgumentNullException>(() =>
            CreateSnapshot(selection, null!, catalogs, meshes)).ParamName);
        Assert.Equal("meshes", Assert.Throws<ArgumentNullException>(() =>
            CreateSnapshot(selection, styles, catalogs, null!)).ParamName);
        Assert.Equal("selection", Assert.Throws<ArgumentNullException>(() =>
            CreateSnapshot(null!, styles, catalogs, meshes)).ParamName);
        Assert.Equal("catalogs", Assert.Throws<ArgumentNullException>(() =>
            CreateSnapshot(selection, styles, null!, meshes)).ParamName);
        var options = new TileWorldViewOptions();
        Assert.Empty(options.AnimatedFoliageStyles);
        Assert.Equal("AnimatedFoliageStyles", Assert.Throws<ArgumentNullException>(() =>
            options.AnimatedFoliageStyles = null!).ParamName);
    }
}
