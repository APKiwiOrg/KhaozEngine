using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>How a catalog's steep companions reach the ground material set: a companion id becomes the slot that
/// id occupies, an omitted band becomes the catalog defaults, and the set refuses a companion on, or naming, its
/// reserved missing-material slot. Headless.</summary>
public class TileGroundSteepCompanionsTests
{
    static TileWorldCatalogs Catalog(string materials) =>
        TileWorldCatalogs.LoadJson($$"""{ "materials": [ {{materials}} ] }""", "memory");

    static TileGroundLayerImage Flat(TileGroundSteepLayer? steep = null) =>
        new() { AlbedoRgba = new byte[4], Steep = steep };

    [Fact]
    public void A_companion_id_becomes_the_slot_that_id_occupies()
    {
        // Ids out of slot order on purpose: 14 is slot 2 because the set sorts ids ascending.
        TileGroundMaterialSet set = TileGroundMaterials.Build(Catalog("""
            { "id": 14, "name": "rock", "color": "#7a7a7a" },
            { "id": 1, "name": "grass", "color": "#4d8a3a", "steep": { "material": 14, "fromDegrees": 35, "toDegrees": 65 } },
            { "id": 2, "name": "dirt", "color": "#8a6a3a" }
            """));

        TileGroundSteepLayer steep = set.Layers[set.SlotOf(1)].Steep!;
        Assert.Equal(set.SlotOf(14), steep.CompanionSlot);
        Assert.Equal(2, steep.CompanionSlot);
        Assert.Equal(35f, steep.FromDegrees);
        Assert.Equal(65f, steep.ToDegrees);
        Assert.Null(set.Layers[set.SlotOf(2)].Steep);
        Assert.Null(set.Layers[set.SlotOf(14)].Steep);
        Assert.Null(set.Layers[set.MissingSlot].Steep);
    }

    [Fact]
    public void An_omitted_band_reaches_the_layer_as_the_catalog_defaults()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(Catalog("""
            { "id": 1, "name": "grass", "color": "#4d8a3a", "steep": { "material": 3 } },
            { "id": 3, "name": "stone", "color": "#7a7a7a" }
            """));

        TileGroundSteepLayer steep = set.Layers[set.SlotOf(1)].Steep!;
        Assert.Equal(GroundMaterialSteep.DefaultFromDegrees, steep.FromDegrees);
        Assert.Equal(GroundMaterialSteep.DefaultToDegrees, steep.ToDegrees);
    }

    [Fact]
    public void A_chain_maps_one_hop_per_material()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(Catalog("""
            { "id": 1, "name": "grass", "color": "#4d8a3a", "steep": { "material": 2 } },
            { "id": 2, "name": "dirt", "color": "#8a6a3a", "steep": { "material": 3 } },
            { "id": 3, "name": "stone", "color": "#7a7a7a" }
            """));

        Assert.Equal(set.SlotOf(2), set.Layers[set.SlotOf(1)].Steep!.CompanionSlot);
        Assert.Equal(set.SlotOf(3), set.Layers[set.SlotOf(2)].Steep!.CompanionSlot);
    }

    [Fact]
    public void A_companion_edited_wrong_after_load_is_refused_before_it_can_land_in_the_missing_slot()
    {
        TileWorldCatalogs catalogs = Catalog("""
            { "id": 1, "name": "grass", "color": "#4d8a3a", "steep": { "material": 3 } },
            { "id": 3, "name": "stone", "color": "#7a7a7a" }
            """);
        catalogs.Material(1)!.Steep!.Material = 9;

        var ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));
        Assert.Contains("ground material set", ex.Message);
        Assert.Contains("not in the same catalog", ex.Message);
    }

    [Fact]
    public void A_hand_built_set_refuses_a_companion_on_the_missing_slot()
    {
        var layers = new[] { Flat(), Flat(new TileGroundSteepLayer { CompanionSlot = 0, FromDegrees = 40f, ToDegrees = 55f }) };
        var ex = Assert.Throws<ArgumentException>(() => new TileGroundMaterialSet(1, 1, new ushort[] { 1 }, layers));
        Assert.Contains("reserved missing-material layer", ex.Message);
    }

    [Fact]
    public void A_hand_built_set_refuses_a_companion_naming_the_missing_slot()
    {
        var layers = new[] { Flat(new TileGroundSteepLayer { CompanionSlot = 2, FromDegrees = 40f, ToDegrees = 55f }), Flat(), Flat() };
        var ex = Assert.Throws<ArgumentException>(() => new TileGroundMaterialSet(1, 1, new ushort[] { 1, 2 }, layers));
        Assert.Contains("not one of the set's material slots 0 to 1", ex.Message);
    }

    [Fact]
    public void A_hand_built_set_refuses_a_band_the_packer_would_refuse()
    {
        var layers = new[] { Flat(new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 60f, ToDegrees = 40f }), Flat(), Flat() };
        var ex = Assert.Throws<ArgumentException>(() => new TileGroundMaterialSet(1, 1, new ushort[] { 1, 2 }, layers));
        Assert.Contains("must rise strictly inside (0, 90)", ex.Message);
    }

    [Fact]
    public void A_sound_hand_built_set_packs_its_companion()
    {
        var layers = new List<TileGroundLayerImage>
        {
            Flat(new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 40f, ToDegrees = 55f }),
            Flat(),
            Flat(),
        };
        var set = new TileGroundMaterialSet(1, 1, new ushort[] { 1, 2 }, layers);

        Vector4[] tail = TileGroundMaterialConfig.BuildParams(set.Layers, 0.15f);
        Assert.Equal(2f, tail[TileGroundMaterialConfig.SteepBandIndex].X);
        Assert.Equal(Vector4.Zero, tail[TileGroundMaterialConfig.SteepBandIndex + set.MissingSlot]);
    }
}
