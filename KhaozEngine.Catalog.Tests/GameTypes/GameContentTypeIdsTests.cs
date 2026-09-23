using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The thirteen shared game type ids and keys, and the finding-code table, pinned.
/// <para>
/// An id is a wire number and a key is the stored spelling, so both are pinned LITERALLY here rather than
/// read back off the constants that produced them. A pack authored against one id table and read against
/// another decodes the wrong rows without ever failing a checksum, and a renamed key is a content change
/// that has to go red before it ships.
/// </para>
/// </summary>
public class GameContentTypeIdsTests
{
    [Fact]
    public void TheThirteenIdsAndKeysArePinned()
    {
        Assert.Equal(
            new (ushort, string)[]
            {
                (1024, "food"),
                (1025, "equip_profile"),
                (1026, "equip_stat_line"),
                (1027, "store"),
                (1028, "store_shelf"),
                (1029, "monster_drop"),
                (1030, "gathering_node"),
                (1031, "recipe"),
                (1032, "recipe_input"),
                (1033, "recipe_output"),
                (1034, "tool_tier"),
                (1035, "skill_curve"),
                (1036, "game_tuning"),
            },
            GameContentTypeIds.TypeKeys.ToArray());
    }

    [Fact]
    public void EveryIdSitsInTheGameBand()
        => Assert.All(GameContentTypeIds.TypeKeys, pair => Assert.True(new ContentTypeId(pair.Id).IsGame));

    [Fact]
    public void TheIdsAreAscendingAndContiguous()
    {
        ushort[] ids = GameContentTypeIds.TypeKeys.Select(pair => pair.Id).ToArray();

        Assert.Equal(Enumerable.Range(ids[0], ids.Length).Select(i => (ushort)i).ToArray(), ids);
    }

    [Fact]
    public void EquipProfileTakesTheEnginesOwnKeyRatherThanASecondSpelling()
    {
        // The engine item type's equip_profile field is a key reference LATE BOUND to whatever registers
        // under this key. A second literal would drift and leave the field pointing at nothing, so the
        // constant is declared as the engine's own and this holds the pair together either way.
        Assert.Equal(EngineContentTypes.EquipProfileTypeKey, GameContentTypeIds.EquipProfileKey);
    }

    [Fact]
    public void EveryFindingCodeIsAUniqueKgtCode()
    {
        string[] codes = Codes();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("KGT", code, StringComparison.Ordinal));
        Assert.All(codes, code => Assert.Equal(7, code.Length));
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheFindingBandsRunAscendingByContentType()
    {
        // A band is a hundred wide and says which type a code came from without a lookup. Only the sweep's
        // 1300 band belongs to no single type, and no code is ever assigned outside a declared band.
        int[] bands = Codes()
            .Select(code => int.Parse(code.AsSpan(3), CultureInfo.InvariantCulture) / 100)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13 }, bands);
    }

    static string[] Codes() => typeof(GameContentFindings)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}
