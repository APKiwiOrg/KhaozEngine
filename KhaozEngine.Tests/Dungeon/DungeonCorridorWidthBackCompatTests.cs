using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // Proves the corridor-width + hall feature is a pure no-op at its defaults (CorridorMinWidth == CorridorMaxWidth
    // == 1, HallChancePercent == 0): the width/hall draws are guarded so they never touch the "rooms" RNG stream,
    // and the corridor carve reduces exactly to the legacy single-line geometry. The golden hashes below were
    // captured on the pre-feature generator; if a guard leaks a draw or the width-1 geometry drifts, they change.
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
        [InlineData(0UL, 0x3602D17138BAA37BUL)]
        [InlineData(1UL, 0x38F8189C95E586FBUL)]
        [InlineData(100UL, 0x58754A0351744A6DUL)]
        [InlineData(999UL, 0xF393BC0C943B0765UL)]
        public void Full_DefaultConfig_ReproducesPreFeatureHash(ulong seed, ulong expected)
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
