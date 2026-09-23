using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The rows and registries the <c>KEC0100</c> band's facts share. Every row builder hands back a CLEAN row
/// by default, so a fact names only the one field it is breaking and the reader sees the defect without
/// reading the fixture.
/// <para>
/// Every registry a fact builds is its OWN, through <see cref="InstanceContentTypes.Register"/> beside the
/// engine types, so nothing here writes process-global state and no <c>DisableParallelization</c>
/// collection is needed.
/// </para>
/// </summary>
internal static class InstanceValidationFixtures
{
    /// <summary>
    /// A fresh registry carrying every engine type and all eighteen of the band, with the candidate
    /// table load index attached to <c>mod</c> when a fact is about the boot that builds it.
    /// </summary>
    public static ContentTypeRegistry Registry(ModCandidateTablesIndex? modCandidateTables = null)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InstanceContentTypes.Register(registry, modCandidateTables);
        return registry;
    }

    /// <summary>
    /// The band's own sweep over one candidate, with no Scope A finding in the way. The rule set is an
    /// argument here exactly as it is on the sweep, so a fact about a removal names the rules it means.
    /// </summary>
    public static List<ContentFinding> Band(
        IContentSnapshot candidate,
        IContentSnapshot? previous = null,
        IReadOnlyList<RemapRule>? rules = null)
    {
        var findings = new List<ContentFinding>();
        InstanceContentChecks.Run(candidate, previous, rules ?? [], findings);
        return findings;
    }

    /// <summary>The one finding carrying that code, failing the fact when there is not exactly one.</summary>
    public static ContentFinding Only(IReadOnlyList<ContentFinding> findings, string code)
    {
        var matches = new List<ContentFinding>();
        foreach (ContentFinding finding in findings)
        {
            if (string.Equals(finding.Code, code, System.StringComparison.Ordinal))
            {
                matches.Add(finding);
            }
        }

        return Assert.Single(matches);
    }

    /// <summary>Every finding, one per line, which is what a failing assertion is handed.</summary>
    public static string Describe(IReadOnlyList<ContentFinding> findings)
    {
        var lines = new List<string>(findings.Count);
        foreach (ContentFinding finding in findings)
        {
            lines.Add(finding.Code + " " + finding.Message);
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    /// <summary>Asserts no finding carries that code, naming every finding when one does.</summary>
    public static void NoneOf(IReadOnlyList<ContentFinding> findings, string code)
        => Assert.DoesNotContain(
            findings,
            finding => string.Equals(finding.Code, code, System.StringComparison.Ordinal));

    static ContentRow Make(ContentTypeRegistry registry, string typeKey, int id, string key, params ContentFieldValue[] fields)
        => RowAt(Lookup(registry, typeKey), id, key, fields);

    static ContentFieldValue Optional(int? id)
        => id is int value
            ? ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, value)
            : ContentFieldValue.Absent(ContentFieldKind.KeyReference);

    static ContentFieldValue Flag(bool value) => ContentFieldValue.OfNumber(ContentFieldKind.Bool, value ? 1 : 0);

    /// <summary>One tag row, which every weight and every socket rule points at.</summary>
    public static ContentRow Tag(ContentTypeRegistry registry, int id, string key)
        => Make(registry, EngineContentTypes.TagTypeKey, id, key, Marker(), ContentFieldValue.Absent(ContentFieldKind.Int));

    /// <summary>One stat row, which every stat line points at.</summary>
    public static ContentRow Stat(ContentTypeRegistry registry, int id, string key)
        => Make(
            registry,
            EngineContentTypes.StatTypeKey,
            id,
            key,
            Marker(),
            Int(1),
            Int(0),
            Int(1000),
            ContentFieldValue.Absent(ContentFieldKind.TagList),
            Marker());

    /// <summary>
    /// One item base, which a unique template and a currency cost point at. Its authored TAG LIST is what
    /// the generation tag signature is interned from, so a fact about the candidate tables hands one in.
    /// </summary>
    public static ContentRow Item(ContentTypeRegistry registry, int id, string key, IReadOnlyList<int>? tags = null)
        => Make(
            registry,
            EngineContentTypes.ItemTypeKey,
            id,
            key,
            Marker(),
            Marker(),
            tags is null ? ContentFieldValue.Absent(ContentFieldKind.TagList) : ContentRowCodecBase.TagListValue(tags),
            Flag(false),
            Int(1),
            Flag(true),
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 10),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            Int(0),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference));

    public static ContentRow Mod(
        ContentTypeRegistry registry,
        int id,
        string key,
        int kind = ModContentType.PrefixKind,
        int? group = null,
        bool legacy = false)
        => Make(registry, InstanceContentTypeIds.ModTypeKey, id, key, Int(kind), Optional(group), Flag(legacy), Marker());

    public static ContentRow ModGroup(ContentTypeRegistry registry, int id, string key, int maxPerItem = 1)
        => Make(registry, InstanceContentTypeIds.ModGroupTypeKey, id, key, Int(maxPerItem));

    public static ContentRow ModTier(
        ContentTypeRegistry registry,
        int id,
        string key,
        int modId,
        int ordinal,
        int itemLevelMin = 1,
        int itemLevelMax = ModTierContentType.MaxItemLevel)
        => Make(
            registry,
            InstanceContentTypeIds.ModTierTypeKey,
            id,
            key,
            Reference(modId),
            Int(ordinal),
            Int(itemLevelMin),
            Int(itemLevelMax));

    public static ContentRow ModTierWeight(
        ContentTypeRegistry registry,
        int id,
        string key,
        int tierId,
        int tagId,
        int weight)
        => Make(
            registry,
            InstanceContentTypeIds.ModTierWeightTypeKey,
            id,
            key,
            Reference(tierId),
            Reference(tagId),
            Int(weight));

    public static ContentRow StatLine(
        ContentTypeRegistry registry,
        int id,
        string key,
        int tierId,
        int statId,
        int sort = 1,
        int combine = StatLineContentType.CombineFlat,
        int min = 10,
        int max = 40)
        => Make(
            registry,
            InstanceContentTypeIds.StatLineTypeKey,
            id,
            key,
            Reference(tierId),
            Int(sort),
            Reference(statId),
            Int(combine),
            Int(min),
            Int(max),
            ContentFieldValue.Absent(ContentFieldKind.TagList),
            ContentFieldValue.Absent(ContentFieldKind.Int));

    public static ContentRow RarityRule(
        ContentTypeRegistry registry,
        int id,
        string key,
        int minAffixes = 0,
        int maxAffixes = 0,
        int maxPrefixes = 0,
        int maxSuffixes = 0,
        int nameWordPositions = 0,
        int? upgradeFrom = null,
        byte[]? displayRgb = null)
        => Make(
            registry,
            InstanceContentTypeIds.RarityRuleTypeKey,
            id,
            key,
            Marker(),
            Int(minAffixes),
            Int(maxAffixes),
            Int(maxPrefixes),
            Int(maxSuffixes),
            Int(nameWordPositions),
            Optional(upgradeFrom),
            displayRgb is null
                ? ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes)
                : ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, displayRgb));

    public static ContentRow RarityWeight(
        ContentTypeRegistry registry,
        int id,
        string key,
        int rarityId,
        int tagId,
        int weight)
        => Make(
            registry,
            InstanceContentTypeIds.RarityWeightTypeKey,
            id,
            key,
            Reference(rarityId),
            Reference(tagId),
            Int(weight));

    public static ContentRow RarityKindLimit(
        ContentTypeRegistry registry,
        int id,
        string key,
        int rarityId,
        int modKind = RarityKindLimitContentType.MinModKind,
        int maxCount = 1)
        => Make(
            registry,
            InstanceContentTypeIds.RarityKindLimitTypeKey,
            id,
            key,
            Reference(rarityId),
            Int(modKind),
            Int(maxCount));

    public static ContentRow UniqueTemplate(
        ContentTypeRegistry registry,
        int id,
        string key,
        int baseId,
        int itemLevelMin = 1,
        int weight = 100)
        => Make(
            registry,
            InstanceContentTypeIds.UniqueTemplateTypeKey,
            id,
            key,
            Reference(baseId),
            Marker(),
            Int(itemLevelMin),
            Int(weight));

    public static ContentRow UniqueLine(
        ContentTypeRegistry registry,
        int id,
        string key,
        int templateId,
        int modId,
        int tierOrdinal = 1,
        int sort = 1)
        => Make(
            registry,
            InstanceContentTypeIds.UniqueLineTypeKey,
            id,
            key,
            Reference(templateId),
            Int(sort),
            Reference(modId),
            Int(tierOrdinal));

    public static ContentRow UniqueSocket(
        ContentTypeRegistry registry,
        int id,
        string key,
        int templateId,
        int sort = 0,
        int? socketTypeId = null)
        => Make(
            registry,
            InstanceContentTypeIds.UniqueSocketTypeKey,
            id,
            key,
            Reference(templateId),
            Int(sort),
            Optional(socketTypeId));

    public static ContentRow SocketType(ContentTypeRegistry registry, int id, string key, int maxNestedBytes = 0)
        => Make(registry, InstanceContentTypeIds.SocketTypeTypeKey, id, key, Marker(), Int(maxNestedBytes));

    public static ContentRow SocketTagRule(
        ContentTypeRegistry registry,
        int id,
        string key,
        int socketTypeId,
        int tagId,
        int rule = SocketTagRuleContentType.RuleAccept,
        int sort = 1)
        => Make(
            registry,
            InstanceContentTypeIds.SocketTagRuleTypeKey,
            id,
            key,
            Reference(socketTypeId),
            Int(sort),
            Reference(tagId),
            Int(rule));

    public static ContentRow RareNameWord(ContentTypeRegistry registry, int id, string key, int position = 1)
        => Make(registry, InstanceContentTypeIds.RareNameWordTypeKey, id, key, Marker(), Int(position));

    public static ContentRow RareNameWordWeight(
        ContentTypeRegistry registry,
        int id,
        string key,
        int wordId,
        int tagId,
        int weight)
        => Make(
            registry,
            InstanceContentTypeIds.RareNameWordWeightTypeKey,
            id,
            key,
            Reference(wordId),
            Reference(tagId),
            Int(weight));

    public static ContentRow Currency(
        ContentTypeRegistry registry,
        int id,
        string key,
        int? consumes = null,
        int consumesCount = 0,
        int maxSteps = 2)
        => Make(
            registry,
            InstanceContentTypeIds.CraftingCurrencyTypeKey,
            id,
            key,
            Marker(),
            Marker(),
            Optional(consumes),
            Int(consumesCount),
            Int(maxSteps));

    public static ContentRow Step(
        ContentTypeRegistry registry,
        int id,
        string key,
        int currencyId,
        int sort = 1,
        int operation = 13)
        => Make(
            registry,
            InstanceContentTypeIds.CurrencyStepTypeKey,
            id,
            key,
            Reference(currencyId),
            Int(sort),
            Int(operation),
            Int(0),
            Int(0),
            Int(0),
            Int(0));

    public static ContentRow Guard(
        ContentTypeRegistry registry,
        int id,
        string key,
        int currencyId,
        int? stepId = null,
        int sort = 1,
        int guardKind = 12)
        => Make(
            registry,
            InstanceContentTypeIds.CurrencyGuardTypeKey,
            id,
            key,
            Reference(currencyId),
            Optional(stepId),
            Int(sort),
            Int(guardKind),
            Int(0),
            Int(0));

    /// <summary>
    /// One valid authored set touching all eighteen types, the shape spec 8.10's checklist describes. It is
    /// authored HERE rather than shipped, because the engine ships eighteen shapes and no rows.
    /// </summary>
    public static ContentRow[] Authored(ContentTypeRegistry registry) =>
    [
        Tag(registry, 5, "metal"),
        Tag(registry, 6, "gem"),
        Stat(registry, 3, "attack"),
        Item(registry, 40, "greatsword"),
        Item(registry, 41, "whetstone"),
        ModGroup(registry, 1, "added_attack"),
        Mod(registry, 1, "sharp", group: 1),
        ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
        ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 1000),
        StatLine(registry, 1, "sharp_t1_l1", tierId: 1, statId: 3),
        Mod(registry, 2, "sunbrand_line"),
        ModTier(registry, 2, "sunbrand_line_t1", modId: 2, ordinal: 1),
        StatLine(registry, 2, "sunbrand_line_t1_l1", tierId: 2, statId: 3),
        RarityRule(registry, 1, "rare", minAffixes: 4, maxAffixes: 6, maxPrefixes: 3, maxSuffixes: 3, nameWordPositions: 2),
        RarityWeight(registry, 1, "rare_metal", rarityId: 1, tagId: 5, weight: 1200),
        RarityKindLimit(registry, 1, "rare_implicit", rarityId: 1),
        UniqueTemplate(registry, 1, "sunbrand", baseId: 40, itemLevelMin: 60, weight: 850),
        UniqueLine(registry, 1, "sunbrand_line_1", templateId: 1, modId: 2),
        UniqueSocket(registry, 1, "sunbrand_socket_1", templateId: 1, socketTypeId: 1),
        SocketType(registry, 1, "gem_socket"),
        SocketTagRule(registry, 1, "gem_socket_accepts_gem", socketTypeId: 1, tagId: 6),
        RareNameWord(registry, 1, "gloom", position: 1),
        RareNameWord(registry, 2, "blade", position: 2),
        RareNameWordWeight(registry, 1, "gloom_metal", wordId: 1, tagId: 5, weight: 400),
        RareNameWordWeight(registry, 2, "blade_metal", wordId: 2, tagId: 5, weight: 400),
        Currency(registry, 1, "whetstone", consumes: 41, consumesCount: 1, maxSteps: 2),
        Step(registry, 1, "whetstone_1", currencyId: 1, sort: 1, operation: 11),
        Step(registry, 2, "whetstone_2", currencyId: 1, sort: 2, operation: 12),
        Guard(registry, 1, "whetstone_target_1", currencyId: 1),
        Guard(registry, 2, "whetstone_1_g1", currencyId: 1, stepId: 1, guardKind: 9),
    ];
}
