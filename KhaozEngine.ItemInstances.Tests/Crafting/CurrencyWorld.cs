using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The authored currency rows the plan, registry and executor facts share, plus the one worked currency
/// spec 20 phase 5's acceptance clause asks for.
/// <para>
/// <b>Every row here is AUTHORED by a test and none of it is shipped.</b> The engine ships eighteen shapes
/// and zero rows, so a currency lives in a fixture or in a game's pack and nowhere else. The worked one is
/// spec 10.4's whetstone extended, and it is deliberately not a currency out of any published game.
/// </para>
/// <para>
/// Every registry and snapshot built here is its OWN, so nothing writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
internal static class CurrencyWorld
{
    /// <summary>The worked currency: a polishing kit that repairs, polishes, sockets and marks.</summary>
    public const int PolishingKit = 7;

    /// <summary>The item one run of the polishing kit spends.</summary>
    public const int PolishFlask = 44;

    /// <summary>The quality ceiling the polish step's own guard authors, inclusive.</summary>
    public const int PolishCeiling = 19;

    /// <summary>How much one polish step adds, as a delta.</summary>
    public const int PolishDelta = 2;

    /// <summary>Kind 1's mirrored bit, which the kit's last step sets.</summary>
    public const int MirroredBit = 1;

    /// <summary>The first operation number a game registers, restated so a fact reads without the lookup.</summary>
    public const int FirstGameOperation = CurrencyStepContentType.FirstGameOperation;

    /// <summary>
    /// The generation world plus the polish flask the kit spends, which is the base every currency fact
    /// authors against.
    /// </summary>
    public static CraftWorld World(Func<ContentTypeRegistry, IEnumerable<ContentRow>>? extra = null)
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.Add(Base(registry, PolishFlask, "polish_flask", [MetalTag], durabilityMax: 0, socketMax: 0));
        if (extra is not null)
        {
            rows.AddRange(extra(registry));
        }

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Dagger);
    }

    /// <summary>
    /// The worked currency of spec 10.4, extended from the whetstone's two steps to four so one of them can
    /// be guarded and skipped.
    /// <list type="number">
    /// <item><description>A TARGET guard of <c>IsIdentified(1)</c>, so an unidentified item refuses the
    /// whole craft and spends nothing.</description></item>
    /// <item><description>Step 1, <c>Repair</c> in full.</description></item>
    /// <item><description>Step 2, <c>SetQuality</c> by a delta, behind a STEP guard of
    /// <c>QualityBetween(0, 19)</c>, so an item already polished skips this step and keeps the
    /// rest.</description></item>
    /// <item><description>Step 3, <c>AddSocket</c> of the gem socket type.</description></item>
    /// <item><description>Step 4, <c>SetFlag</c> of the mirrored bit.</description></item>
    /// </list>
    /// </summary>
    public static IEnumerable<ContentRow> PolishingKitRows(ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return
        [
            Currency(registry, PolishingKit, "polishing_kit", consumes: PolishFlask, consumesCount: 1, maxSteps: 4),
            Step(registry, 71, "polishing_kit_1", PolishingKit, sort: 1, operation: 11),
            Step(
                registry,
                72,
                "polishing_kit_2",
                PolishingKit,
                sort: 2,
                operation: 12,
                a: PolishDelta),
            Step(registry, 73, "polishing_kit_3", PolishingKit, sort: 3, operation: 6, a: GemSocket),
            Step(registry, 74, "polishing_kit_4", PolishingKit, sort: 4, operation: 14, a: MirroredBit, b: 1),
            Guard(registry, 75, "polishing_kit_target", PolishingKit, guardKind: (int)CraftGuardKind.IsIdentified, a: 1),
            Guard(
                registry,
                76,
                "polishing_kit_2_g1",
                PolishingKit,
                stepId: 72,
                guardKind: (int)CraftGuardKind.QualityBetween,
                a: 0,
                b: PolishCeiling),
        ];
    }

    /// <summary>The world carrying the worked currency, which is what the executor facts run.</summary>
    public static CraftWorld PolishingWorld() => World(PolishingKitRows);

    /// <summary>One <c>crafting_currency</c> row.</summary>
    public static ContentRow Currency(
        ContentTypeRegistry registry,
        int id,
        string key,
        int? consumes = null,
        int consumesCount = 0,
        int maxSteps = 4)
        => RowAt(
            Lookup(registry, InstanceContentTypeIds.CraftingCurrencyTypeKey),
            id,
            key,
            Marker(),
            Marker(),
            consumes is int spent
                ? Reference(spent)
                : ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            Int(consumesCount),
            Int(maxSteps));

    /// <summary>One <c>currency_step</c> row, with its four parameters in authored order.</summary>
    public static ContentRow Step(
        ContentTypeRegistry registry,
        int id,
        string key,
        int currencyId,
        int sort,
        int operation,
        int a = 0,
        int b = 0,
        int c = 0,
        int d = 0)
        => RowAt(
            Lookup(registry, InstanceContentTypeIds.CurrencyStepTypeKey),
            id,
            key,
            Reference(currencyId),
            Int(sort),
            Int(operation),
            Int(a),
            Int(b),
            Int(c),
            Int(d));

    /// <summary>
    /// One <c>currency_guard</c> row. An absent <paramref name="stepId"/> is what makes it a TARGET guard,
    /// which is spec 10.4's one empty reference and not a second type.
    /// </summary>
    public static ContentRow Guard(
        ContentTypeRegistry registry,
        int id,
        string key,
        int currencyId,
        int? stepId = null,
        int sort = 1,
        int guardKind = (int)CraftGuardKind.IsIdentified,
        int a = 0,
        int b = 0)
        => RowAt(
            Lookup(registry, InstanceContentTypeIds.CurrencyGuardTypeKey),
            id,
            key,
            Reference(currencyId),
            stepId is int step ? Reference(step) : ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            Int(sort),
            Int(guardKind),
            Int(a),
            Int(b));

    /// <summary>One built load index over that world, which is the boot half of a currency.</summary>
    public static CraftPlanIndex Index(CraftWorld world, CraftingRegistry? operations = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var index = new CraftPlanIndex(operations);
        index.Build(world.Snapshot);
        return index;
    }

    /// <summary>The plan one currency resolved to, failing the fact rather than handing back a null.</summary>
    public static CraftPlan Plan(CraftWorld world, int currencyId)
    {
        Assert.True(Index(world).TryGetPlan(currencyId, out CraftPlan? plan));
        return plan;
    }

    /// <summary>One executor over that world, on the source and the operations the fact hands in.</summary>
    public static CraftExecutor Executor(
        CraftWorld world,
        IRandomSource random,
        CraftingRegistry? operations = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new CraftExecutor(
            world.Snapshot,
            GenerationTables.Build(ModCandidateTables.Build(world.Snapshot), world.Snapshot),
            operations ?? Frozen(new CraftingRegistry()),
            random);
    }

    /// <summary>A registry frozen the way a pack load freezes one, which is how a host hands one over.</summary>
    public static CraftingRegistry Frozen(CraftingRegistry operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        operations.Freeze();
        return operations;
    }

    /// <summary>
    /// One target the polishing kit runs on: identified or not, at a chosen quality, with durability to
    /// repair and one affix to prove the craft left the rest of the payload alone.
    /// </summary>
    public static byte[] Target(
        bool identified = true,
        int quality = 5,
        int durability = 30,
        int durabilityMaximum = 120,
        int itemLevel = 60,
        int rarityId = MagicRarity,
        uint flags = 0)
    {
        var builder = new ItemInstancePayloadBuilder();
        if (flags != 0)
        {
            _ = builder.AddScalar(InstancePropertyKind.Flags, flags);
        }

        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, (ulong)itemLevel);
        if (quality > 0)
        {
            _ = builder.AddScalar(InstancePropertyKind.Quality, (ulong)quality);
        }

        _ = builder.AddScalars(InstancePropertyKind.Durability, (ulong)durability, (ulong)durabilityMaximum);
        _ = builder.AddIdentification(identified, revealedMask: 0);
        _ = builder.AddByte(InstancePropertyKind.Rarity, (byte)rarityId);
        _ = builder.AddAffixes(InstancePropertyKind.Affixes, [new InstanceAffix(1, 1, RollPosition.Bottom)]);
        return builder.ToArray();
    }
}
