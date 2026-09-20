namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Every finding code the validators over these types emit, written down exactly once.
/// </summary>
/// <remarks>
/// A code is a STABLE TOKEN rather than a message: a counter, a test and an operator runbook all key on it,
/// which is the same thing the engine's <c>KEC</c> codes are and why this table takes their shape. The
/// engine folds a game-band finding into <c>KEC0040</c> and puts the code below in the message text, so this
/// token is what an operator reads off a refused publish.
/// <para>
/// ONE table, because two spellings of the same idea is how a token stops being stable. Every validator and
/// every test names a constant from here, and no literal code is written anywhere else.
/// </para>
/// <para>
/// <b>The numbering is BANDED by content type, ascending by type id, a hundred to a band.</b> A band is wide
/// enough that a type gaining a rule appends rather than renumbering, and the band a code sits in says which
/// type it came from without a lookup. A type carrying no rule of its own takes no band. A code is never
/// reused and never renumbered: a withdrawn rule leaves its number withdrawn, and the 1200 band is held
/// back rather than filled. 1300 is the cross-type sweep, which belongs to no single type.
/// </para>
/// </remarks>
public static class GameContentFindings
{
    /// <summary>A food row that heals nothing, which is what makes an item food at all.</summary>
    public const string FoodHealsNotPositive = "KGT0101";

    /// <summary>A negative attack delay, which would hand the eater a free attack.</summary>
    public const string FoodAttackDelayNegative = "KGT0102";

    /// <summary>Two food rows claiming one item, which leaves the heal amount ambiguous.</summary>
    public const string FoodDuplicateItem = "KGT0103";

    /// <summary>Two lines of one profile moving one stat.</summary>
    public const string EquipStatLineDuplicatePair = "KGT0201";

    /// <summary>Two lines of one profile claiming one draw position.</summary>
    public const string EquipStatLineDuplicateSort = "KGT0202";

    /// <summary>Two live stores claim the same npc kind, so the shopkeeper resolves to neither.</summary>
    public const string StoreDuplicateNpcKind = "KGT0301";

    /// <summary>A rate below zero, which would pay a player to hand an item over.</summary>
    public const string StoreNegativeRate = "KGT0302";

    /// <summary>
    /// A rate that prices the catalog's most valuable item outside the range the trade path can hold, which
    /// it throws on rather than wrapping a purse.
    /// </summary>
    public const string StoreRateOverCurrencyCeiling = "KGT0303";

    /// <summary>Two live shelves of one store claim one draw position.</summary>
    public const string StoreShelfDuplicateSort = "KGT0401";

    /// <summary>One item appears twice on one store, so the panel draws it twice.</summary>
    public const string StoreShelfDuplicateItem = "KGT0402";

    /// <summary>A shelf names an item that has left play.</summary>
    public const string StoreShelfRetiredItem = "KGT0403";

    /// <summary>Two live rows claim the same creature kind, so a death resolves to neither table.</summary>
    public const string MonsterDropDuplicateMonsterKind = "KGT0501";

    /// <summary>A node holding no lives, which falls before the first yield comes off it.</summary>
    public const string GatheringNodeLivesNotPositive = "KGT0601";

    /// <summary>A node asking for a level below the first one a character has.</summary>
    public const string GatheringNodeLevelRequiredBelowOne = "KGT0602";

    /// <summary>A node yielding an item that has left play.</summary>
    public const string GatheringNodeRetiredYieldItem = "KGT0603";

    /// <summary>Two live recipes claim one list position, so the panel has no order between them.</summary>
    public const string RecipeDuplicateDisplayOrder = "KGT0701";

    /// <summary>A recipe naming a skill nothing can be paid into.</summary>
    public const string RecipeSkillNotPayable = "KGT0702";

    /// <summary>A recipe naming a payable skill that is not open yet, so nobody can be paid by it.</summary>
    public const string RecipeLockedSkill = "KGT0703";

    /// <summary>A recipe asking for a level below the first one a character has.</summary>
    public const string RecipeLevelRequiredBelowOne = "KGT0704";

    /// <summary>A recipe listed under an item that has left play.</summary>
    public const string RecipeRetiredPrimaryItem = "KGT0705";

    /// <summary>A recipe naming no station, which is a place rather than a station a row may name.</summary>
    public const string RecipeStationNone = "KGT0706";

    /// <summary>A recipe naming a station number nothing in the game stands for.</summary>
    public const string RecipeUnknownStation = "KGT0707";

    /// <summary>A recipe that takes no time, which would complete forever within one step of the clock.</summary>
    public const string RecipeBaseDurationNotPositive = "KGT0708";

    /// <summary>A recipe paying nothing, which would train the skill it is listed under at no rate.</summary>
    public const string RecipeXpPerItemNotPositive = "KGT0709";

    /// <summary>A recipe naming a repeat mode number nothing in the game stands for.</summary>
    public const string RecipeUnknownRepeatMode = "KGT0710";

    /// <summary>Two live inputs of one recipe claim one list position.</summary>
    public const string RecipeInputDuplicateSort = "KGT0801";

    /// <summary>An input consuming nothing, which is a line the panel draws and the step ignores.</summary>
    public const string RecipeInputCountNotPositive = "KGT0802";

    /// <summary>An input naming an item that has left play.</summary>
    public const string RecipeInputRetiredItem = "KGT0803";

    /// <summary>Two live outputs of one recipe claim one list position.</summary>
    public const string RecipeOutputDuplicateSort = "KGT0901";

    /// <summary>An output producing nothing, which pays nothing and lands nothing in the bag.</summary>
    public const string RecipeOutputCountNotPositive = "KGT0902";

    /// <summary>An output naming an item that has left play.</summary>
    public const string RecipeOutputRetiredItem = "KGT0903";

    /// <summary>Two tiers of one family at one rank, which leaves the better tool undecided.</summary>
    public const string ToolTierDuplicateRank = "KGT1001";

    /// <summary>A success scale at or below zero, which is a tool that never succeeds.</summary>
    public const string ToolTierSuccessScaleNotPositive = "KGT1002";

    /// <summary>A time scale at or below zero, which is an action that takes no time.</summary>
    public const string ToolTierTimeScaleNotPositive = "KGT1003";

    /// <summary>Two curve rows claiming one skill, which leaves that skill's knobs ambiguous.</summary>
    public const string SkillCurveDuplicateSkill = "KGT1101";

    /// <summary>A curve row naming a skill number nothing in the game stands for.</summary>
    public const string SkillCurveUnknownSkill = "KGT1102";

    /// <summary>An experience rate at or below zero, which is a knob that pays nothing and reads as unset.</summary>
    public const string SkillCurveXpPerDamageNotPositive = "KGT1103";

    /// <summary>A shelf selling an item a player cannot trade, which the buy half of the counter refuses.</summary>
    public const string SweepShelfItemNotTradable = "KGT1301";

    /// <summary>A node quoting a chance over the global ceiling, which the runtime would silently clamp.</summary>
    public const string SweepGatheringChanceOverCeiling = "KGT1302";

    /// <summary>A node gated on a level over the global cap, so nobody can ever work it.</summary>
    public const string SweepGatheringLevelOverCap = "KGT1303";

    /// <summary>A recipe gated on a level over the global cap, so nobody can ever make it.</summary>
    public const string SweepRecipeLevelOverCap = "KGT1304";

    /// <summary>A recipe producing nothing, which costs the time and lands nothing in the bag.</summary>
    public const string SweepRecipeWithoutOutput = "KGT1305";

    /// <summary>A tool tier whose item does not carry the tag its family names, so no swing ever selects it.</summary>
    public const string SweepToolTierItemLacksFamilyTag = "KGT1306";

    /// <summary>A knob the game reads with no row in a tuning table that carries the rest of them.</summary>
    public const string SweepTuningKnobMissing = "KGT1307";
}
