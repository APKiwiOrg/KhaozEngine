using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // Proves the corridor-width + hall feature is a pure no-op at its defaults (CorridorMinWidth == CorridorMaxWidth
    // == 1, HallChancePercent == 0): the width/hall draws are guarded so they never touch the "rooms" RNG stream,
    // and the corridor carve reduces exactly to the legacy single-line geometry. The single-floor Simple hashes are
    // still the original pre-feature goldens (single-floor rasters carry no stair cells, so the three-tread stair
    // change left them byte-identical). The multi-floor Full hashes were re-captured after that stair change (the
    // raster and stair edges legitimately differ); the corridor-width no-op property they guard is still enforced
    // relatively by ExplicitWidthOneAndNoHalls_EqualsImplicitDefault below.
    public class DungeonCorridorWidthBackCompatTests
    {
        static DungeonConfig Simple() => new() { RoomCountTarget = 10, MaxFloors = 1, LockCount = 0, BossRoom = false, LoopEdgeBudget = 0 };
        static DungeonConfig Full() => new() { RoomCountTarget = 14, MaxFloors = 3, LockCount = 2, LoopEdgeBudget = 2 };

        [Theory]
        [InlineData(1UL, 0x72B360D43F29ECDDUL)]
        [InlineData(7UL, 0x705A7659B9AB7FB5UL)]
        [InlineData(42UL, 0x84E69C816E7526DDUL)]
        public void Simple_DefaultConfig_ReproducesPreFeatureHash(ulong seed, ulong expected)
        {
            Assert.Equal(expected, DungeonGenerator.Generate(Simple(), seed).LayoutHash());
        }

        [Theory]
        [InlineData(0UL, 0x34C5B526E908A159UL)]
        [InlineData(1UL, 0x61E11D853207DCDFUL)]
        [InlineData(100UL, 0x3D3430AE12176C40UL)]
        [InlineData(999UL, 0xFDBABC916B1E92E1UL)]
        public void Full_DefaultConfig_ReproducesGoldenHash(ulong seed, ulong expected)
        {
            Assert.Equal(expected, DungeonGenerator.Generate(Full(), seed).LayoutHash());
        }

        [Theory]
        [InlineData(1UL)]
        [InlineData(999UL)]
        public void ExplicitWidthOneAndNoHalls_EqualsImplicitDefault(ulong seed)
        {
            DungeonConfig baseline = Full();
            DungeonConfig explicitDefaults = Full();
            explicitDefaults.CorridorMinWidth = 1;
            explicitDefaults.CorridorMaxWidth = 1;
            explicitDefaults.HallChancePercent = 0;

            Assert.Equal(
                DungeonGenerator.Generate(baseline, seed).LayoutHash(),
                DungeonGenerator.Generate(explicitDefaults, seed).LayoutHash());
        }
    }
}
