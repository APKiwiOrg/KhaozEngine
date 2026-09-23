using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The thirteen content type ids and keys this package owns, written down exactly once.
/// </summary>
/// <remarks>
/// A type id is a wire number: a pack authored against one id table and read against another decodes the
/// wrong rows without ever failing a checksum, so the id and the key live here and nowhere else. Every
/// registration, every migration and every tool names a constant from this file rather than a literal.
/// <para>
/// The ids start at 1024 because that is where <see cref="ContentRegistrationBand.Game"/> starts. Below it
/// sit the engine's own 1 to 255 and the item-instances 256 to 1023, and taking a number out of either is
/// refused at registration rather than discovered at a collision two engine releases later. A game that
/// registers these types spends the block below on them and authors its OWN types above it.
/// </para>
/// <para>
/// The keys are the STORED spelling and are not a game's to rename. Two worlds that author the same facts
/// store them under the same key, which is the whole point of a shared type, and a rename is a content
/// change rather than a tidy-up.
/// </para>
/// <para>
/// This file is a table and holds no logic, deliberately. What each type MEANS is the content type that
/// registers under it.
/// </para>
/// </remarks>
public static class GameContentTypeIds
{
    /// <summary>What eating an item restores and how long it holds the next attack up, type id 1024.</summary>
    public const ushort Food = 1024;

    /// <summary>The <see cref="Food"/> type's stable key.</summary>
    public const string FoodKey = "food";

    /// <summary>Where an equippable is worn, how it swings and how long a swing takes, type id 1025.</summary>
    public const ushort EquipProfile = 1025;

    /// <summary>
    /// The <see cref="EquipProfile"/> type's stable key, which is the ENGINE's own constant rather than a
    /// second copy of its spelling. The item type's <c>equip_profile</c> field is a late binding by KEY, so
    /// a type registered under any other spelling is one nothing ever points at.
    /// </summary>
    public const string EquipProfileKey = EngineContentTypes.EquipProfileTypeKey;

    /// <summary>One stat value of one equip profile, type id 1026.</summary>
    public const ushort EquipStatLine = 1026;

    /// <summary>The <see cref="EquipStatLine"/> type's stable key.</summary>
    public const string EquipStatLineKey = "equip_stat_line";

    /// <summary>One shopkeeper kind and the two rates it trades at, type id 1027.</summary>
    public const ushort Store = 1027;

    /// <summary>The <see cref="Store"/> type's stable key.</summary>
    public const string StoreKey = "store";

    /// <summary>One shelf of one store, in draw order, type id 1028.</summary>
    public const ushort StoreShelf = 1028;

    /// <summary>The <see cref="StoreShelf"/> type's stable key.</summary>
    public const string StoreShelfKey = "store_shelf";

    /// <summary>A creature kind and the <c>loot_table</c> it rolls on death, type id 1029.</summary>
    public const ushort MonsterDrop = 1029;

    /// <summary>The <see cref="MonsterDrop"/> type's stable key.</summary>
    public const string MonsterDropKey = "monster_drop";

    /// <summary>One standing gathering source and what a swing at it pays, type id 1030.</summary>
    public const ushort GatheringNode = 1030;

    /// <summary>The <see cref="GatheringNode"/> type's stable key.</summary>
    public const string GatheringNodeKey = "gathering_node";

    /// <summary>One processing step, its skill, its station and its experience, type id 1031.</summary>
    public const ushort Recipe = 1031;

    /// <summary>The <see cref="Recipe"/> type's stable key.</summary>
    public const string RecipeKey = "recipe";

    /// <summary>One input of one recipe, type id 1032.</summary>
    public const ushort RecipeInput = 1032;

    /// <summary>The <see cref="RecipeInput"/> type's stable key.</summary>
    public const string RecipeInputKey = "recipe_input";

    /// <summary>One output of one recipe, type id 1033.</summary>
    public const ushort RecipeOutput = 1033;

    /// <summary>The <see cref="RecipeOutput"/> type's stable key.</summary>
    public const string RecipeOutputKey = "recipe_output";

    /// <summary>One tier of one ranked tool family, type id 1034.</summary>
    public const ushort ToolTier = 1034;

    /// <summary>The <see cref="ToolTier"/> type's stable key.</summary>
    public const string ToolTierKey = "tool_tier";

    /// <summary>One skill's own knobs, type id 1035.</summary>
    public const ushort SkillCurve = 1035;

    /// <summary>The <see cref="SkillCurve"/> type's stable key.</summary>
    public const string SkillCurveKey = "skill_curve";

    /// <summary>One global knob, a scaled integer, type id 1036.</summary>
    public const ushort GameTuning = 1036;

    /// <summary>The <see cref="GameTuning"/> type's stable key.</summary>
    public const string GameTuningKey = "game_tuning";

    /// <summary>
    /// Every id paired with its key, ASCENDING by id and contiguous. Pinned rather than derived, so an id
    /// added without a row here is a failing test rather than a type the tooling cannot see.
    /// </summary>
    public static IReadOnlyList<(ushort Id, string Key)> TypeKeys { get; } = new (ushort Id, string Key)[]
    {
        (Food, FoodKey),
        (EquipProfile, EquipProfileKey),
        (EquipStatLine, EquipStatLineKey),
        (Store, StoreKey),
        (StoreShelf, StoreShelfKey),
        (MonsterDrop, MonsterDropKey),
        (GatheringNode, GatheringNodeKey),
        (Recipe, RecipeKey),
        (RecipeInput, RecipeInputKey),
        (RecipeOutput, RecipeOutputKey),
        (ToolTier, ToolTierKey),
        (SkillCurve, SkillCurveKey),
        (GameTuning, GameTuningKey),
    };
}
