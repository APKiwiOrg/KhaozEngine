using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Registers all thirteen types in ONE call, each with the validator this package ships for it, the way
/// <see cref="EngineContentTypes.Register"/> does for the engine's own six.
/// </summary>
/// <remarks>
/// The per-type <c>Register</c> calls stay public and a game is free to use them: registering only the
/// types a world actually authors is the ordinary case, and a game that wants a validator of its own on one
/// type needs the narrow call. This is the path for a game that wants the whole set, and it exists because
/// thirteen hand-written calls are thirteen chances to pass a wrong validator, a wrong unit or nothing at
/// all, and none of those three fails loudly.
/// <para>
/// <b>Two types register with NO validator, deliberately.</b> <c>equip_profile</c> carries three durable
/// numbers and no rule a schema does not already make, and every rule about <c>game_tuning</c> is a
/// statement about the SET of rows or about a type that READS a knob, both of which belong to a cross-type
/// pass. The one rule a single tuning row could carry, that its knob name is unique, is already the
/// engine's <c>KEC0002</c>.
/// </para>
/// <para>
/// The order is ASCENDING by type id, which is the order the id table declares and the order the engine
/// walks registrations in.
/// </para>
/// <para>
/// <b>The cross-type sweep rides <c>food</c>'s slot, composed over that type's own validator.</b> The
/// engine's validation model takes one <see cref="IContentValidator"/> per type and hands each of them the
/// WHOLE candidate, and it offers a game no whole-registry slot of its own: the one pass that is not
/// per-type, the item-instances band, is engine code reached through a band registration a game cannot join.
/// So a cross-type check registered on all thirteen would report every defect thirteen times, and the
/// answer is one slot, the lowest game id, running once. <see cref="GameContentChecks"/> is public for a
/// game registering by hand, which mounts it the same way.
/// </para>
/// </remarks>
public static class GameContentTypes
{
    /// <summary>
    /// Registers every type this package owns on <paramref name="registry"/>, in the game band, under the
    /// ids and keys <see cref="GameContentTypeIds"/> declares and each type's own visibility and chunk
    /// slots, with the package's own validator on every type that has one.
    /// </summary>
    /// <param name="registry">
    /// A registry that is not frozen and carries none of these thirteen ids or keys. The engine's own six
    /// go on first, because <c>equip_profile</c> closes a late binding the <c>item</c> type declares and
    /// several rules read an engine schema off this same registry.
    /// </param>
    /// <param name="unit">The game's own time unit, which picks the four duration field names.</param>
    /// <param name="options">Every answer the package needs from the game. Every member is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or already carries one of these ids or keys.
    /// </exception>
    public static void Register(
        ContentTypeRegistry registry,
        ContentDurationUnit unit,
        GameContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        FoodContentType.Register(
            registry,
            unit,
            new GameContentChecks(registry, options.Sweep, new FoodContentType.Validator()));
        EquipProfileContentType.Register(registry, unit, validator: null);
        EquipStatLineContentType.Register(registry, new EquipStatLineContentType.Validator());
        StoreContentType.Register(registry, new StoreContentType.Validator(registry));
        StoreShelfContentType.Register(registry, new StoreShelfContentType.Validator());
        MonsterDropContentType.Register(registry, new MonsterDropContentType.Validator());
        GatheringNodeContentType.Register(registry, unit, new GatheringNodeContentType.Validator());
        RecipeContentType.Register(registry, unit, new RecipeContentType.Validator(options.Recipe));
        RecipeInputContentType.Register(registry, new RecipeInputContentType.Validator());
        RecipeOutputContentType.Register(registry, new RecipeOutputContentType.Validator());
        ToolTierContentType.Register(registry, new ToolTierContentType.Validator());
        SkillCurveContentType.Register(registry, new SkillCurveContentType.Validator(options.IsKnownSkill));
        GameTuningContentType.Register(registry, validator: null);
    }
}
