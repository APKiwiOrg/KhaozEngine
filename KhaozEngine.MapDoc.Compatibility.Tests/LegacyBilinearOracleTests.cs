using System;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyBilinearOracleTests
{
    // LegacyBilinearOracleTests (Compatibility.Tests, public)
    [Fact]
    public void LegacyBilinear_MatchesReleasedHeightAtBitForBit()
    {
        float negative = -1f / 1073741824f;
        Assert.Equal(BitConverter.SingleToInt32Bits(negative - MathF.Floor(negative)), BitConverter.SingleToInt32Bits(new MapExactValue(1073741823, 1073741824).ToSingle()));
        Assert.Equal(1f, new MapExactValue(1073741823, 1073741824).ToSingle());
        LegacyOracleWorld w = LegacyOracleWorld.Create();
        float[] fractions = { 0f, 0.25f, 0.5f, 0.75f, 0.9990234375f };
        foreach (string name in new[] { "void", "flag-nodraw", "extreme", "seam-west", "derived-plane-1" })
        {
            LegacyOracleCase c = w.Cases.Single(x => x.Name == name);
            var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, RegionCoord.Of(c.WorldX, c.WorldZ), c.Plane);
            foreach (float fx in fractions)
                foreach (float fz in fractions)
                {
                    float worldX = c.WorldX + fx, worldZ = -(c.WorldZ + fz);
                    float released = w.Document.HeightAt(worldX, worldZ, c.Plane);
                    float native = MapLegacyBilinear.HeightMetres(surface, patch, new MapExactXz(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ)));
                    Assert.Equal(BitConverter.SingleToInt32Bits(released), BitConverter.SingleToInt32Bits(native));
                }
        }
    }
}
