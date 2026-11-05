using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileFoliageLayerRefusalTests
{
    [Theory]
    [InlineData(TileWorldRefusal.FoliageIdBlank)]
    [InlineData(TileWorldRefusal.FoliagePlaneNegative)]
    [InlineData(TileWorldRefusal.FoliageOriginNonFinite)]
    [InlineData(TileWorldRefusal.FoliageSampleLimitExceeded)]
    [InlineData(TileWorldRefusal.FoliageSpacingInvalid)]
    [InlineData(TileWorldRefusal.FoliageScaleRangeInvalid)]
    [InlineData(TileWorldRefusal.FoliageRootOffsetNonFinite)]
    [InlineData(TileWorldRefusal.FoliageDoorClearanceInvalid)]
    [InlineData(TileWorldRefusal.FoliageEdgeFadeInvalid)]
    [InlineData(TileWorldRefusal.FoliageArchetypesEmpty)]
    [InlineData(TileWorldRefusal.FoliageArchetypeDuplicate)]
    [InlineData(TileWorldRefusal.FoliageTotalWeightNonFinite)]
    [InlineData(TileWorldRefusal.FoliageUnderlayVoid)]
    [InlineData(TileWorldRefusal.FoliageLayerNull)]
    [InlineData(TileWorldRefusal.FoliageArchetypesNull)]
    [InlineData(TileWorldRefusal.FoliageArchetypeNull)]
    public void Constructor_and_dto_refusals_identify_the_broken_rule(TileWorldRefusal reason)
    {
        Assert.Equal("meadow", TileWorldFile.FoliageFromDto(ValidDto()).Id);
        TileFoliageLayerDto dto = InvalidDto(reason);
        var ex = Assert.Throws<TileFoliageArgumentException>(() => TileWorldFile.FoliageFromDto(dto));
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Null(ex.ParamName);
        Assert.Equal(reason, ex.Reason);
        AssertIdentifiers(ex);
        if (reason == TileWorldRefusal.FoliageLayerNull) Assert.Null(ex.LayerIndex);
    }

    // The sample limit is inclusive: a raster of exactly MaxDensitySamples is a layer, so a guard tightened by one
    // fails here while the overflow row above still refuses.
    [Fact]
    public void A_raster_of_exactly_the_sample_limit_is_a_layer()
    {
        TileFoliageLayerDto dto = ValidDto();
        dto.Width = 4096;
        dto.Height = TileFoliageLayer.MaxDensitySamples / dto.Width;
        dto.Density = new byte[TileFoliageLayer.MaxDensitySamples];
        TileFoliageLayer layer = TileWorldFile.FoliageFromDto(dto);
        Assert.Equal(TileFoliageLayer.MaxDensitySamples, (long)layer.Width * layer.Height);
    }

    [Theory]
    [InlineData(TileWorldRefusal.FoliageIdBlank)]
    [InlineData(TileWorldRefusal.FoliagePlaneNegative)]
    [InlineData(TileWorldRefusal.FoliageOriginNonFinite)]
    [InlineData(TileWorldRefusal.FoliageCellSizeInvalid)]
    [InlineData(TileWorldRefusal.FoliageDimensionsNonPositive)]
    [InlineData(TileWorldRefusal.FoliageSampleLimitExceeded)]
    [InlineData(TileWorldRefusal.FoliageDensityLengthMismatch)]
    [InlineData(TileWorldRefusal.FoliageSpacingInvalid)]
    [InlineData(TileWorldRefusal.FoliageScaleRangeInvalid)]
    [InlineData(TileWorldRefusal.FoliageRootOffsetNonFinite)]
    [InlineData(TileWorldRefusal.FoliageDoorClearanceInvalid)]
    [InlineData(TileWorldRefusal.FoliageEdgeFadeInvalid)]
    [InlineData(TileWorldRefusal.FoliageArchetypesEmpty)]
    [InlineData(TileWorldRefusal.FoliageArchetypeIdBlank)]
    [InlineData(TileWorldRefusal.FoliageArchetypeDuplicate)]
    [InlineData(TileWorldRefusal.FoliageWeightInvalid)]
    [InlineData(TileWorldRefusal.FoliageTotalWeightNonFinite)]
    [InlineData(TileWorldRefusal.FoliageUnderlayVoid)]
    [InlineData(TileWorldRefusal.FoliageLayerNull)]
    [InlineData(TileWorldRefusal.FoliageArchetypesNull)]
    [InlineData(TileWorldRefusal.FoliageArchetypeNull)]
    [InlineData(TileWorldRefusal.FoliageLayerDuplicate)]
    [InlineData(TileWorldRefusal.FoliagePlaneOutsideWorld)]
    [InlineData(TileWorldRefusal.FoliageDensityNull)]
    public void Manifest_refusals_preserve_the_exact_reason_identifiers_and_cause(TileWorldRefusal reason)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetFoliageLayer(TileWorldFile.FoliageFromDto(ValidDto()));
        TileWorldFile.Save(doc, dir);
        Assert.Single(TileWorldSource.Open(dir).Document.FoliageLayers);
        string path = TileWorldFile.ManifestPath(dir);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var rows = new List<TileFoliageLayerDto> { InvalidDto(reason) };
        if (reason == TileWorldRefusal.FoliageLayerNull) rows.Insert(0, ValidDto());
        if (reason == TileWorldRefusal.FoliageLayerDuplicate) rows.Add(ValidDto());
        var options = new JsonSerializerOptions(TileWorldJson.Manifest)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        // A normal JSON number outside float32 range reaches the same finite-value guards as direct input.
        string json = JsonSerializer.Serialize(rows, options).Replace("\"Infinity\"", "1e40", StringComparison.Ordinal);
        root["foliageLayers"] = JsonNode.Parse(json);
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldSource.Open(dir));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        if (reason == TileWorldRefusal.FoliageDensityNull)
        {
            Assert.Equal("meadow", ex.LayerId);
            Assert.Equal("density", ex.Field);
            Assert.Equal("density", Assert.IsType<ArgumentNullException>(ex.InnerException).ParamName);
            return;
        }
        var inner = Assert.IsType<TileFoliageArgumentException>(ex.InnerException);
        Assert.Equal(reason, inner.Reason);
        AssertIdentifiers(inner);
        Assert.Equal(inner.LayerId, ex.LayerId);
        Assert.Equal(inner.Plane, ex.Plane);
        Assert.Equal(inner.PlaneCount, ex.PlaneCount);
        Assert.Equal(inner.OriginX, ex.OriginX);
        Assert.Equal(inner.OriginZ, ex.OriginZ);
        Assert.Equal(inner.CellSize, ex.CellSize);
        Assert.Equal(inner.Width, ex.Width);
        Assert.Equal(inner.Height, ex.Height);
        Assert.Equal(inner.SampleCount, ex.SampleCount);
        Assert.Equal(inner.MaximumSampleCount, ex.MaximumSampleCount);
        Assert.Equal(inner.ActualCount, ex.ActualCount);
        Assert.Equal(inner.ExpectedCount, (long?)ex.ExpectedCount);
        Assert.Equal(inner.Spacing, ex.Spacing);
        Assert.Equal(inner.ScaleMin, ex.ScaleMin);
        Assert.Equal(inner.ScaleMax, ex.ScaleMax);
        Assert.Equal(inner.RootOffset, ex.RootOffset);
        Assert.Equal(inner.DoorClearance, ex.DoorClearance);
        Assert.Equal(inner.EdgeFade, ex.EdgeFade);
        Assert.Equal(inner.ArchetypeCount, ex.ArchetypeCount);
        Assert.Equal(inner.ArchetypeId, ex.ArchetypeId);
        Assert.Equal(inner.ArchetypeIndex, ex.ArchetypeIndex);
        Assert.Equal(inner.Weight, ex.Weight);
        Assert.Equal(inner.TotalWeight, ex.TotalWeight);
        Assert.Equal(inner.MaterialId, ex.MaterialId);
        Assert.Equal(inner.LayerIndex, ex.LayerIndex);
        if (reason == TileWorldRefusal.FoliageLayerNull) Assert.Equal(1, ex.LayerIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Document_plane_admission_refuses_before_mutation_or_save(bool duringSave)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileFoliageLayerDto dto = ValidDto();
        dto.Plane = 4;
        TileFoliageLayer layer = TileWorldFile.FoliageFromDto(dto);
        var doc = new TileWorldDocument { PlaneCount = 5 };
        doc.SetFoliageLayer(layer);
        TileWorldFile.Save(doc, dir);
        string path = TileWorldFile.ManifestPath(dir);
        byte[] before = File.ReadAllBytes(path);
        if (!duringSave) doc.RemoveFoliageLayer(layer.Id);
        doc.PlaneCount = 4;
        var ex = Assert.Throws<TileFoliageArgumentException>(() =>
        {
            if (duringSave) TileWorldFile.Save(doc, dir);
            else doc.SetFoliageLayer(layer);
        });
        Assert.Equal(TileWorldRefusal.FoliagePlaneOutsideWorld, ex.Reason);
        Assert.Equal("meadow", ex.LayerId);
        Assert.Equal(4, ex.Plane);
        Assert.Equal(4, ex.PlaneCount);
        Assert.Equal(before, File.ReadAllBytes(path));
        if (duringSave) Assert.Same(layer, Assert.Single(doc.FoliageLayers));
        else Assert.Empty(doc.FoliageLayers);
    }

    [Theory]
    [InlineData("density")]
    [InlineData("archetypes")]
    public void Direct_null_parameters_keep_their_Bcl_type_and_parameter_name(string field)
    {
        TileFoliageLayerDto dto = ValidDto();
        Assert.Equal("meadow", TileWorldFile.FoliageFromDto(dto).Id);
        var ex = Assert.Throws<ArgumentNullException>(() => new TileFoliageLayer(
            dto.Id, dto.Plane, dto.OriginX, dto.OriginZ, dto.CellSize, dto.Width, dto.Height,
            field == "density" ? null! : dto.Density, dto.Seed, dto.Spacing, dto.ScaleMin,
            dto.ScaleMax, dto.RootOffset, field == "archetypes" ? null! : [new("bush", 1f)]));
        Assert.Equal(field, ex.ParamName);
    }

    static TileFoliageLayerDto ValidDto() => new()
    {
        Id = "meadow", Plane = 0, OriginX = 1.5f, OriginZ = -2.5f, CellSize = 1f,
        Width = 1, Height = 1, Density = [255], Seed = 1, Spacing = 1f,
        ScaleMin = 1f, ScaleMax = 1f, RootOffset = 0f,
        Archetypes = [new() { Id = "bush", Weight = 1f }], AllowedUnderlays = [1],
    };

    static TileFoliageLayerDto InvalidDto(TileWorldRefusal reason)
    {
        TileFoliageLayerDto dto = ValidDto();
        switch (reason)
        {
            case TileWorldRefusal.FoliageIdBlank: dto.Id = " "; break;
            case TileWorldRefusal.FoliagePlaneNegative: dto.Plane = -1; break;
            case TileWorldRefusal.FoliageOriginNonFinite: dto.OriginX = float.PositiveInfinity; break;
            case TileWorldRefusal.FoliageCellSizeInvalid: dto.CellSize = 0f; break;
            case TileWorldRefusal.FoliageDimensionsNonPositive: dto.Width = 0; dto.Density = []; break;
            case TileWorldRefusal.FoliageSampleLimitExceeded: dto.Width = int.MaxValue; dto.Height = 2; break;
            case TileWorldRefusal.FoliageDensityLengthMismatch: dto.Density = [1, 2]; break;
            case TileWorldRefusal.FoliageSpacingInvalid: dto.Spacing = 0f; break;
            case TileWorldRefusal.FoliageScaleRangeInvalid: dto.ScaleMin = 2f; break;
            case TileWorldRefusal.FoliageRootOffsetNonFinite: dto.RootOffset = float.PositiveInfinity; break;
            case TileWorldRefusal.FoliageDoorClearanceInvalid: dto.DoorClearance = -1f; break;
            case TileWorldRefusal.FoliageEdgeFadeInvalid: dto.EdgeFade = -1f; break;
            case TileWorldRefusal.FoliageArchetypesEmpty: dto.Archetypes.Clear(); break;
            case TileWorldRefusal.FoliageArchetypeIdBlank: dto.Archetypes.Add(new() { Id = "", Weight = 1f }); break;
            case TileWorldRefusal.FoliageArchetypeDuplicate: dto.Archetypes.Add(new() { Id = "bush", Weight = 1f }); break;
            case TileWorldRefusal.FoliageWeightInvalid: dto.Archetypes[0].Weight = 0f; break;
            case TileWorldRefusal.FoliageTotalWeightNonFinite:
                dto.Archetypes = [new() { Id = "bush", Weight = float.MaxValue }, new() { Id = "tree", Weight = float.MaxValue }];
                break;
            case TileWorldRefusal.FoliageUnderlayVoid: dto.AllowedUnderlays = [0]; break;
            case TileWorldRefusal.FoliageLayerNull: return null!;
            case TileWorldRefusal.FoliageArchetypesNull: dto.Archetypes = null!; break;
            case TileWorldRefusal.FoliageArchetypeNull: dto.Archetypes.Add(null!); break;
            case TileWorldRefusal.FoliagePlaneOutsideWorld: dto.Plane = 4; break;
            case TileWorldRefusal.FoliageDensityNull: dto.Density = null!; break;
            case TileWorldRefusal.FoliageLayerDuplicate: break;
            default: throw new ArgumentOutOfRangeException(nameof(reason));
        }
        return dto;
    }

    static void AssertIdentifiers(TileFoliageArgumentException ex)
    {
        Assert.Equal(ex.Reason == TileWorldRefusal.FoliageIdBlank ? " " :
            ex.Reason == TileWorldRefusal.FoliageLayerNull ? null : "meadow", ex.LayerId);
        switch (ex.Reason)
        {
            case TileWorldRefusal.FoliagePlaneNegative: Assert.Equal(-1, ex.Plane); break;
            case TileWorldRefusal.FoliageOriginNonFinite:
                Assert.Equal(float.PositiveInfinity, ex.OriginX); Assert.Equal(-2.5f, ex.OriginZ); break;
            case TileWorldRefusal.FoliageCellSizeInvalid: Assert.Equal(0f, ex.CellSize); break;
            case TileWorldRefusal.FoliageDimensionsNonPositive:
                Assert.Equal(0, ex.Width); Assert.Equal(1, ex.Height); break;
            case TileWorldRefusal.FoliageSampleLimitExceeded:
                Assert.Equal(4_294_967_294L, ex.SampleCount);
                Assert.Equal(TileFoliageLayer.MaxDensitySamples, ex.MaximumSampleCount); break;
            case TileWorldRefusal.FoliageDensityLengthMismatch:
                Assert.Equal(2, ex.ActualCount); Assert.Equal(1L, ex.ExpectedCount);
                Assert.Equal(1, ex.Width); Assert.Equal(1, ex.Height); break;
            case TileWorldRefusal.FoliageSpacingInvalid: Assert.Equal(0f, ex.Spacing); break;
            case TileWorldRefusal.FoliageScaleRangeInvalid:
                Assert.Equal(2f, ex.ScaleMin); Assert.Equal(1f, ex.ScaleMax); break;
            case TileWorldRefusal.FoliageRootOffsetNonFinite: Assert.Equal(float.PositiveInfinity, ex.RootOffset); break;
            case TileWorldRefusal.FoliageDoorClearanceInvalid: Assert.Equal(-1f, ex.DoorClearance); break;
            case TileWorldRefusal.FoliageEdgeFadeInvalid: Assert.Equal(-1f, ex.EdgeFade); break;
            case TileWorldRefusal.FoliageArchetypesEmpty: Assert.Equal(0, ex.ArchetypeCount); break;
            case TileWorldRefusal.FoliageArchetypeIdBlank:
                Assert.Equal("", ex.ArchetypeId); Assert.Equal(1, ex.ArchetypeIndex); break;
            case TileWorldRefusal.FoliageArchetypeDuplicate: Assert.Equal("bush", ex.ArchetypeId); break;
            case TileWorldRefusal.FoliageWeightInvalid:
                Assert.Equal("bush", ex.ArchetypeId); Assert.Equal(0f, ex.Weight); break;
            case TileWorldRefusal.FoliageTotalWeightNonFinite: Assert.Equal(float.PositiveInfinity, ex.TotalWeight); break;
            case TileWorldRefusal.FoliageUnderlayVoid: Assert.Equal((ushort)0, ex.MaterialId); break;
            case TileWorldRefusal.FoliageArchetypeNull: Assert.Equal(1, ex.ArchetypeIndex); break;
            case TileWorldRefusal.FoliagePlaneOutsideWorld:
                Assert.Equal(4, ex.Plane); Assert.Equal(4, ex.PlaneCount); break;
            case TileWorldRefusal.FoliageIdBlank:
            case TileWorldRefusal.FoliageLayerNull:
            case TileWorldRefusal.FoliageArchetypesNull:
            case TileWorldRefusal.FoliageLayerDuplicate: break;
            default: throw new ArgumentOutOfRangeException(nameof(ex));
        }
    }
}
