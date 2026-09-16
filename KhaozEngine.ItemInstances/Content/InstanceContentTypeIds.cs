using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The eighteen affix content type ids and keys of spec 8.1, assigned in ONE block and never reused
/// (contracts 5.1). Eighteen of the 768 ids the <see cref="ContentRegistrationBand.Instances"/> band
/// covers, leaving 750.
/// <para>
/// The whole table lives here even while only part of it is implemented, because the ids are allocated
/// once. A later type cannot renumber, and an implementer does not have to re-derive the assignment from
/// the spec to add the next one.
/// </para>
/// <para>
/// <b>Seven of them are the types an author thinks in, and eleven are their CHILDREN.</b> The parent
/// column of spec 8.1's table, restated so this file answers the shape question on its own:
/// </para>
/// <list type="table">
///   <listheader><term>Type</term><description>Parent</description></listheader>
///   <item><term><c>mod</c></term><description>none</description></item>
///   <item><term><c>mod_group</c></term><description>none</description></item>
///   <item><term><c>rarity_rule</c></term><description>none</description></item>
///   <item><term><c>unique_template</c></term><description>none</description></item>
///   <item><term><c>socket_type</c></term><description>none</description></item>
///   <item><term><c>crafting_currency</c></term><description>none</description></item>
///   <item><term><c>rare_name_word</c></term><description>none</description></item>
///   <item><term><c>mod_tier</c></term><description><c>mod</c></description></item>
///   <item><term><c>mod_tier_weight</c></term><description><c>mod_tier</c></description></item>
///   <item><term><c>stat_line</c></term><description><c>mod_tier</c></description></item>
///   <item><term><c>rarity_weight</c></term><description><c>rarity_rule</c></description></item>
///   <item><term><c>rarity_kind_limit</c></term><description><c>rarity_rule</c></description></item>
///   <item><term><c>unique_line</c></term><description><c>unique_template</c></description></item>
///   <item><term><c>unique_socket</c></term><description><c>unique_template</c></description></item>
///   <item><term><c>socket_tag_rule</c></term><description><c>socket_type</c></description></item>
///   <item><term><c>currency_step</c></term><description><c>crafting_currency</c></description></item>
///   <item><term><c>currency_guard</c></term><description><c>crafting_currency</c></description></item>
///   <item><term><c>rare_name_word_weight</c></term><description><c>rare_name_word</c></description></item>
/// </list>
/// <para>
/// A child row carries its id, its key, a key reference to its parent, and a <c>sort</c> where its ORDER
/// matters. A child whose rows have no order (a weight, a kind limit) has no <c>sort</c>.
/// </para>
/// <para>
/// Three of the eighteen are <see cref="ContentVisibility.ServerOnly"/> as WHOLE TYPES, the three weights,
/// because the client chunk builder omits whole FIELDS and a weight buried inside a mixed-visibility row
/// had no way out. A client downloads no weight row at all.
/// </para>
/// </summary>
public static class InstanceContentTypeIds
{
    /// <summary>One affix: its kind, its group and its display line. Type id 256.</summary>
    public const ushort ModTypeId = 256;

    /// <summary>The mod type's stable key.</summary>
    public const string ModTypeKey = "mod";

    /// <summary>An exclusivity group and how many of it one item may carry. Type id 257.</summary>
    public const ushort ModGroupTypeId = 257;

    /// <summary>The mod group type's stable key.</summary>
    public const string ModGroupTypeKey = "mod_group";

    /// <summary>How many affixes a rarity permits, and its composed name template. Type id 258.</summary>
    public const ushort RarityRuleTypeId = 258;

    /// <summary>The rarity rule type's stable key.</summary>
    public const string RarityRuleTypeKey = "rarity_rule";

    /// <summary>A fixed item built on a base. Type id 259.</summary>
    public const ushort UniqueTemplateTypeId = 259;

    /// <summary>The unique template type's stable key.</summary>
    public const string UniqueTemplateTypeKey = "unique_template";

    /// <summary>What a socket accepts. Type id 260.</summary>
    public const ushort SocketTypeTypeId = 260;

    /// <summary>
    /// The socket type's stable key, which is the key <c>base_socket.socket_type</c> already points at.
    /// The engine writes the key once, in <see cref="EngineContentTypes.SocketTypeTypeKey"/>, and this
    /// band is what registers a type under it, so the late binding resolves at registry freeze.
    /// </summary>
    public const string SocketTypeTypeKey = EngineContentTypes.SocketTypeTypeKey;

    /// <summary>A named sequence of steps with guards. Type id 261.</summary>
    public const ushort CraftingCurrencyTypeId = 261;

    /// <summary>The crafting currency type's stable key.</summary>
    public const string CraftingCurrencyTypeKey = "crafting_currency";

    /// <summary>One word in a rare-name position. Type id 262.</summary>
    public const ushort RareNameWordTypeId = 262;

    /// <summary>The rare name word type's stable key.</summary>
    public const string RareNameWordTypeKey = "rare_name_word";

    /// <summary>One tier of one mod, with its item level gate. Child of <c>mod</c>. Type id 263.</summary>
    public const ushort ModTierTypeId = 263;

    /// <summary>The mod tier type's stable key.</summary>
    public const string ModTierTypeKey = "mod_tier";

    /// <summary>One tier's spawn weight against one tag. Child of <c>mod_tier</c>. Type id 264.</summary>
    public const ushort ModTierWeightTypeId = 264;

    /// <summary>The mod tier weight type's stable key.</summary>
    public const string ModTierWeightTypeKey = "mod_tier_weight";

    /// <summary>One stat a tier grants, with its range. Child of <c>mod_tier</c>. Type id 265.</summary>
    public const ushort StatLineTypeId = 265;

    /// <summary>The stat line type's stable key.</summary>
    public const string StatLineTypeKey = "stat_line";

    /// <summary>One rarity's weight against one tag. Child of <c>rarity_rule</c>. Type id 266.</summary>
    public const ushort RarityWeightTypeId = 266;

    /// <summary>The rarity weight type's stable key.</summary>
    public const string RarityWeightTypeKey = "rarity_weight";

    /// <summary>
    /// How many of one mod kind above 2 a rarity permits. Child of <c>rarity_rule</c>. Type id 267.
    /// </summary>
    public const ushort RarityKindLimitTypeId = 267;

    /// <summary>The rarity kind limit type's stable key.</summary>
    public const string RarityKindLimitTypeKey = "rarity_kind_limit";

    /// <summary>
    /// One mod, at one tier, that a unique template grants. Child of <c>unique_template</c>. Type id 268.
    /// </summary>
    public const ushort UniqueLineTypeId = 268;

    /// <summary>The unique line type's stable key.</summary>
    public const string UniqueLineTypeKey = "unique_line";

    /// <summary>
    /// One socket a unique template forces, in authored order. Child of <c>unique_template</c>. Type id 269.
    /// </summary>
    public const ushort UniqueSocketTypeId = 269;

    /// <summary>The unique socket type's stable key.</summary>
    public const string UniqueSocketTypeKey = "unique_socket";

    /// <summary>One accept or reject tag of a socket type. Child of <c>socket_type</c>. Type id 270.</summary>
    public const ushort SocketTagRuleTypeId = 270;

    /// <summary>The socket tag rule type's stable key.</summary>
    public const string SocketTagRuleTypeKey = "socket_tag_rule";

    /// <summary>
    /// One step: a primitive or a game operation, with its parameters. Child of <c>crafting_currency</c>.
    /// Type id 271.
    /// </summary>
    public const ushort CurrencyStepTypeId = 271;

    /// <summary>The currency step type's stable key.</summary>
    public const string CurrencyStepTypeKey = "currency_step";

    /// <summary>
    /// One guard, on a currency's target or on one of its steps. Child of <c>crafting_currency</c>.
    /// Type id 272.
    /// </summary>
    public const ushort CurrencyGuardTypeId = 272;

    /// <summary>The currency guard type's stable key.</summary>
    public const string CurrencyGuardTypeKey = "currency_guard";

    /// <summary>One word's weight against one tag. Child of <c>rare_name_word</c>. Type id 273.</summary>
    public const ushort RareNameWordWeightTypeId = 273;

    /// <summary>The rare name word weight type's stable key.</summary>
    public const string RareNameWordWeightTypeKey = "rare_name_word_weight";
}
