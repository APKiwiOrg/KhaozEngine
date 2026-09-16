using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE helper that registers all eighteen affix content types of spec 8.1, mirroring
/// <see cref="EngineContentTypes.Register"/>. Eighteen ids out of the Instances band's 768 are spent,
/// leaving 750.
/// <para>
/// A host calls this once, at process start and before any pack loads, which is what makes "does this
/// process use instances" one line in a boot sequence. A second call on the same registry THROWS, because
/// the ids are already taken, and so does a call against a frozen registry.
/// </para>
/// <para>
/// <b>The band's own validator hangs off ONE registration, the lowest id in the band.</b> Every check of
/// spec 8.9 is CROSS TYPE, over a parent and its children or over rarities, words and tags together, so
/// there is no honest way to split it into eighteen per-type validators and attaching the same instance to
/// all eighteen would run the whole band eighteen times. The sweep's pass 6 iterates the band ascending,
/// so the checks run once, first, and before any game validator.
/// </para>
/// <para>
/// <see cref="InstanceContentValidator"/> carries no state and reads nothing ambient, so the single
/// instance built here is safe to share across every registry a process builds.
/// </para>
/// </summary>
public static class InstanceContentTypes
{
    /// <summary>
    /// Registers all eighteen instance content types, once, at process start and before any pack loads.
    /// </summary>
    /// <param name="registry">The registry the eighteen declarations land in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or one of the eighteen ids or keys is already taken, which a second call to
    /// this helper on the same registry is.
    /// </exception>
    public static void Register(ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        RegisterOne(
            registry,
            InstanceContentTypeIds.ModTypeId,
            InstanceContentTypeIds.ModTypeKey,
            ModContentType.CreateSchema(),
            ModContentType.DefaultVisibility,
            ModContentType.DefaultChunkSlots,
            ModContentType.MaxRowBytes,
            static (type, schema) => new ModContentType.Codec(type, schema),
            validator: new InstanceContentValidator());

        RegisterOne(
            registry,
            InstanceContentTypeIds.ModGroupTypeId,
            InstanceContentTypeIds.ModGroupTypeKey,
            ModGroupContentType.CreateSchema(),
            ModGroupContentType.DefaultVisibility,
            ModGroupContentType.DefaultChunkSlots,
            ModGroupContentType.MaxRowBytes,
            static (type, schema) => new ModGroupContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.RarityRuleTypeId,
            InstanceContentTypeIds.RarityRuleTypeKey,
            RarityRuleContentType.CreateSchema(),
            RarityRuleContentType.DefaultVisibility,
            RarityRuleContentType.DefaultChunkSlots,
            RarityRuleContentType.MaxRowBytes,
            static (type, schema) => new RarityRuleContentType.Codec(type, schema),
            maxDefinitionId: RarityRuleContentType.MaxDefinitionId);

        RegisterOne(
            registry,
            InstanceContentTypeIds.UniqueTemplateTypeId,
            InstanceContentTypeIds.UniqueTemplateTypeKey,
            UniqueTemplateContentType.CreateSchema(),
            UniqueTemplateContentType.DefaultVisibility,
            UniqueTemplateContentType.DefaultChunkSlots,
            UniqueTemplateContentType.MaxRowBytes,
            static (type, schema) => new UniqueTemplateContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.SocketTypeTypeId,
            InstanceContentTypeIds.SocketTypeTypeKey,
            SocketTypeContentType.CreateSchema(),
            SocketTypeContentType.DefaultVisibility,
            SocketTypeContentType.DefaultChunkSlots,
            SocketTypeContentType.MaxRowBytes,
            static (type, schema) => new SocketTypeContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.CraftingCurrencyTypeId,
            InstanceContentTypeIds.CraftingCurrencyTypeKey,
            CraftingCurrencyContentType.CreateSchema(),
            CraftingCurrencyContentType.DefaultVisibility,
            CraftingCurrencyContentType.DefaultChunkSlots,
            CraftingCurrencyContentType.MaxRowBytes,
            static (type, schema) => new CraftingCurrencyContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.RareNameWordTypeId,
            InstanceContentTypeIds.RareNameWordTypeKey,
            RareNameWordContentType.CreateSchema(),
            RareNameWordContentType.DefaultVisibility,
            RareNameWordContentType.DefaultChunkSlots,
            RareNameWordContentType.MaxRowBytes,
            static (type, schema) => new RareNameWordContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.ModTierTypeId,
            InstanceContentTypeIds.ModTierTypeKey,
            ModTierContentType.CreateSchema(),
            ModTierContentType.DefaultVisibility,
            ModTierContentType.DefaultChunkSlots,
            ModTierContentType.MaxRowBytes,
            static (type, schema) => new ModTierContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.ModTierWeightTypeId,
            InstanceContentTypeIds.ModTierWeightTypeKey,
            ModTierWeightContentType.CreateSchema(),
            ModTierWeightContentType.DefaultVisibility,
            ModTierWeightContentType.DefaultChunkSlots,
            ModTierWeightContentType.MaxRowBytes,
            static (type, schema) => new ModTierWeightContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.StatLineTypeId,
            InstanceContentTypeIds.StatLineTypeKey,
            StatLineContentType.CreateSchema(),
            StatLineContentType.DefaultVisibility,
            StatLineContentType.DefaultChunkSlots,
            StatLineContentType.MaxRowBytes,
            static (type, schema) => new StatLineContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.RarityWeightTypeId,
            InstanceContentTypeIds.RarityWeightTypeKey,
            RarityWeightContentType.CreateSchema(),
            RarityWeightContentType.DefaultVisibility,
            RarityWeightContentType.DefaultChunkSlots,
            RarityWeightContentType.MaxRowBytes,
            static (type, schema) => new RarityWeightContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.RarityKindLimitTypeId,
            InstanceContentTypeIds.RarityKindLimitTypeKey,
            RarityKindLimitContentType.CreateSchema(),
            RarityKindLimitContentType.DefaultVisibility,
            RarityKindLimitContentType.DefaultChunkSlots,
            RarityKindLimitContentType.MaxRowBytes,
            static (type, schema) => new RarityKindLimitContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.UniqueLineTypeId,
            InstanceContentTypeIds.UniqueLineTypeKey,
            UniqueLineContentType.CreateSchema(),
            UniqueLineContentType.DefaultVisibility,
            UniqueLineContentType.DefaultChunkSlots,
            UniqueLineContentType.MaxRowBytes,
            static (type, schema) => new UniqueLineContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.UniqueSocketTypeId,
            InstanceContentTypeIds.UniqueSocketTypeKey,
            UniqueSocketContentType.CreateSchema(),
            UniqueSocketContentType.DefaultVisibility,
            UniqueSocketContentType.DefaultChunkSlots,
            UniqueSocketContentType.MaxRowBytes,
            static (type, schema) => new UniqueSocketContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.SocketTagRuleTypeId,
            InstanceContentTypeIds.SocketTagRuleTypeKey,
            SocketTagRuleContentType.CreateSchema(),
            SocketTagRuleContentType.DefaultVisibility,
            SocketTagRuleContentType.DefaultChunkSlots,
            SocketTagRuleContentType.MaxRowBytes,
            static (type, schema) => new SocketTagRuleContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.CurrencyStepTypeId,
            InstanceContentTypeIds.CurrencyStepTypeKey,
            CurrencyStepContentType.CreateSchema(),
            CurrencyStepContentType.DefaultVisibility,
            CurrencyStepContentType.DefaultChunkSlots,
            CurrencyStepContentType.MaxRowBytes,
            static (type, schema) => new CurrencyStepContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.CurrencyGuardTypeId,
            InstanceContentTypeIds.CurrencyGuardTypeKey,
            CurrencyGuardContentType.CreateSchema(),
            CurrencyGuardContentType.DefaultVisibility,
            CurrencyGuardContentType.DefaultChunkSlots,
            CurrencyGuardContentType.MaxRowBytes,
            static (type, schema) => new CurrencyGuardContentType.Codec(type, schema));

        RegisterOne(
            registry,
            InstanceContentTypeIds.RareNameWordWeightTypeId,
            InstanceContentTypeIds.RareNameWordWeightTypeKey,
            RareNameWordWeightContentType.CreateSchema(),
            RareNameWordWeightContentType.DefaultVisibility,
            RareNameWordWeightContentType.DefaultChunkSlots,
            RareNameWordWeightContentType.MaxRowBytes,
            static (type, schema) => new RareNameWordWeightContentType.Codec(type, schema));
    }

    static void RegisterOne(
        ContentTypeRegistry registry,
        ushort typeId,
        string typeKey,
        ContentFieldSchema schema,
        ContentVisibility visibility,
        int chunkSlots,
        int maxRowBytes,
        Func<ContentTypeId, ContentFieldSchema, IContentRowCodec> codec,
        IContentValidator? validator = null,
        int? maxDefinitionId = null)
        => registry.RegisterContentType(
            ContentRegistrationBand.Instances,
            typeId,
            typeKey,
            codec(new ContentTypeId(typeId), schema),
            validator,
            schema,
            visibility,
            chunkSlots,
            maxRowBytes,
            maxDefinitionId);
}
