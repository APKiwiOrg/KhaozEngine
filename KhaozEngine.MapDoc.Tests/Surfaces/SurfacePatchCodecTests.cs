using System;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfacePatchCodecTests
{
    [Fact]
    public void Patch_RoundTripsExactIntegersAndSignedSlots()
    {
        MapSurfacePatch p = SurfacePatchFixtures.Sample();
        byte[] bytes = MapSurfacePatchCodec.Encode(p);
        MapSurfacePatch back = MapSurfacePatchCodec.Decode(bytes, p.Key);
        Assert.Equal(p.Heights, back.Heights);
        Assert.Equal(p.Cells, back.Cells);
        Assert.Equal((false, true), (back.IsPresent(1, 0), back.IsPresent(0, 1)));
        Assert.Equal(MapLatticeAddress.Corner(-4, 0), back.CornerAddress(0, 0));
        Assert.Equal(bytes, MapSurfacePatchCodec.Encode(back));
        Assert.Contains("\"slotX\":\"-1\"", Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public void PatchKey_FloorsNegativeCellsIntoSlots()
    {
        Assert.Equal(new MapPatchKey("g", -1, -1), MapPatchKey.ForCell("g", -1, -64));
        Assert.Equal(new MapPatchKey("g", -2, 0), MapPatchKey.ForCell("g", -65, 63));
        Assert.Equal(new MapPatchKey("g", 1, 0), MapPatchKey.ForCell("g", 64, 0));
        Assert.True(new MapPatchKey("a", 5, 5).CompareTo(new MapPatchKey("b", -9, -9)) < 0);
        Assert.True(new MapPatchKey("a", 9, -1).CompareTo(new MapPatchKey("a", 0, 0)) < 0);
    }
    [Theory, InlineData("width", "\"width\":4", "\"width\":65"), InlineData("heights", "\"depth\":2", "\"depth\":3"),
     InlineData("presence", "/QAAAAAAAAA=", "/QAAAAABAAA="), InlineData("unknown", "\"width\":4", "\"width\":4,\"extra\":1")]
    public void Decode_RefusesMalformedPayloadBeforeAllocation(string member, string from, string to)
    {
        string text = Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(SurfacePatchFixtures.Sample()));
        Assert.Contains(from, text);
        byte[] bad = Encoding.UTF8.GetBytes(text.Replace(from, to));
        Assert.Contains(member, Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(bad, SurfacePatchFixtures.Sample().Key)).Message);
    }
    [Fact]
    public void Decode_RefusesOversizedInputUnreducedUnitsAndOverflow()
    {
        Assert.Contains("1048576", Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(new byte[1_048_577], SurfacePatchFixtures.Sample().Key)).Message);
        Assert.Contains("reduced", Assert.Throws<MapDocumentException>(() => new MapRational(2, 200)).Message);
        Assert.Contains("positive", Assert.Throws<MapDocumentException>(() => new MapRational(0, 1)).Message);
        Assert.Equal(new MapExactPoint(new(4, 1), new(1433, 100), new(-64, 1)), MapLatticeFrame.ImportedMetreCentimetre.Corner(4, 64, 1433));
        Assert.Contains("overflow", Assert.Throws<MapExactOverflowException>(() => new MapExactValue(long.MaxValue, 1).Multiply(new(2, 1))).Message);
    }

    [Theory]
    [InlineData(4, 4, "cut")]
    [InlineData(5, 4, "rotation")]
    [InlineData(6, 128, "flags")]
    [InlineData(7, 3, "topology")]
    public void Decode_RejectsInvalidPackedCellBytes(int offset, int value, string diagnostic)
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Sample();
        JsonObject json = JsonNode.Parse(MapSurfacePatchCodec.Encode(patch))!.AsObject();
        byte[] cells = Convert.FromBase64String(json["cells"]!.GetValue<string>());
        cells[offset] = (byte)value;
        json["cells"] = Convert.ToBase64String(cells);
        Assert.Contains(diagnostic, Assert.Throws<MapDocumentException>(() =>
            MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), patch.Key)).Message);
    }

    [Fact]
    public void Patch_MetadataOrderingIsCanonicalAndCloneOwnsMutableStorage()
    {
        MapSurfacePatch patch = SurfacePatchFixtures.Row(3);
        patch.EdgeSubdivisions.Add(new(2, 0, MapCellEdge.East, 3));
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 2));
        patch.CornerDependencies.Add(new(1, 0, new(new("other", 0, 0), MapLatticeAddress.Corner(1, 0))));
        patch.CornerDependencies.Add(new(0, 0, new(new("other", 0, 0), MapLatticeAddress.Corner(0, 0))));
        byte[] encoded = MapSurfacePatchCodec.Encode(patch);
        MapSurfacePatch clone = patch.Clone();
        clone.EdgeSubdivisions.Reverse();
        clone.CornerDependencies.Reverse();
        Assert.Equal(encoded, MapSurfacePatchCodec.Encode(clone));
        Assert.Equal(encoded, MapSurfacePatchCodec.Encode(MapSurfacePatchCodec.Decode(encoded, patch.Key)));
        Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(encoded, new("wrong", 0, 0)));
        clone.Heights[0] = 12;
        clone.Cells[0] = default;
        clone.SetPresent(0, 0, false);
        clone.CornerDependencies.Clear();
        Assert.Equal(0, patch.Heights[0]);
        Assert.Equal((ushort)1, patch.Cells[0].Underlay);
        Assert.True(patch.IsPresent(0, 0));
        Assert.Equal(2, patch.CornerDependencies.Count);
    }
}
