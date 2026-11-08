using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceMetadataRefusalTests
{
    [Theory]
    [InlineData(false, "owner", MapDocumentRefusal.AuthoredValueMismatch, "Key.SurfaceId")]
    [InlineData(false, "frame", MapDocumentRefusal.SchemaValueRequired, "Frame")]
    [InlineData(false, "role", MapDocumentRefusal.SchemaEnumUnsupported, "Role")]
    [InlineData(false, "presence", MapDocumentRefusal.SchemaEnumUnsupported, "PresencePolicy")]
    [InlineData(true, "id", MapDocumentRefusal.SchemaValueRequired, "Id")]
    [InlineData(true, "frame", MapDocumentRefusal.SchemaValueRequired, "Frame")]
    [InlineData(true, "role", MapDocumentRefusal.SchemaEnumUnsupported, "Role")]
    [InlineData(true, "presence", MapDocumentRefusal.SchemaEnumUnsupported, "PresencePolicy")]
    public void Metadata_refuses_the_first_failed_field(bool semantic, string fault, MapDocumentRefusal reason, string field)
    {
        var (original, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var surface = fault switch
        {
            "owner" => original with { Id = "other" },
            "id" => original with { Id = " " },
            "frame" => original with { Frame = null! },
            "role" => original with { Role = (MapSurfaceRole)255 },
            _ => original with { PresencePolicy = (MapPresencePolicy)255 },
        };
        object Run(MapSurfaceRef value) => semantic ? MapSurfaceSemantics.SurfaceDigest(value,
            Array.Empty<KeyValuePair<MapPatchKey, string>>()) : MapSurfaceCompiler.Compile(value, patch);
        var error = Assert.Throws<MapDocumentException>(() => Run(surface));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(surface.Id, error.SurfaceId);
        if (!semantic) Assert.Equal(patch.Key, error.Patch);
        if (reason == MapDocumentRefusal.SchemaEnumUnsupported)
        {
            Assert.Equal(255L, error.ActualInteger);
            Assert.Equal(fault == "role" ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy), error.EnumType);
        }
        Assert.NotNull(Run(original));
    }

    [Fact]
    public void Legacy_compilation_reuses_the_surface_recipe_rule()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        surface = surface with { PresencePolicy = MapPresencePolicy.LegacyTileWorld };
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(surface, patch));
        Assert.Equal(MapDocumentRefusal.SurfaceLegacyRecipeRequired, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal("Frame", error.FieldPath);
        Assert.NotEmpty(MapSurfaceCompiler.Compile(surface with { Frame = MapLatticeFrame.ImportedMetreCentimetre }, patch).Faces);
    }

    [Fact]
    public void Native_NoDraw_refusal_keeps_the_slot_relative_cell()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full with { Flags = MapCellFlags.NoDraw });
        patch.CellMinX = 3;
        patch.CellMinZ = 2;
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(surface, patch));
        Assert.Equal(MapDocumentRefusal.SurfaceNativeNoDrawUnsupported, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(131, error.SlotCell);
        Assert.Equal("Cells[0].Flags", error.FieldPath);
        Assert.Equal(MapCellFlags.NoDraw, patch.Cells[0].Flags);
        patch.Cells[0] = patch.Cells[0] with { Flags = MapCellFlags.None };
        Assert.NotEmpty(MapSurfaceCompiler.Compile(surface, patch).Faces);
    }

    [Fact]
    public void Height_query_keeps_the_exact_arithmetic_refusal()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 100, 100, 200, CompilerFixtures.Full);
        // The height is x + z. Coprime denominators multiply past int64, while either coordinate fits.
        var x = new MapExactValue(1, long.MaxValue);
        var z = new MapExactValue(1, long.MaxValue - 1);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.ExactHeight(surface, patch, x, z));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(nameof(MapSurfaceCompiler.ExactHeight), error.Operation);
        Assert.Equal(new MapExactValue(2, long.MaxValue), MapSurfaceCompiler.ExactHeight(surface, patch, x, x));
    }
}
