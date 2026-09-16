using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Crafting.CurrencyWorld;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The game operation registry of spec 10.5, which is <c>InstancePropertyRegistry</c>'s shape one level
/// over: registration once at process start, the freeze at the first pack load, a later registration that
/// throws, and a caller band the registry itself refuses rather than a rule written in prose.
/// <para>
/// <b>The registry is PER INSTANCE and never a static</b>, which is the #349 rule applied before it bites.
/// A static registry would be process-global state every crafting fact writes, which would force a
/// <c>DisableParallelization</c> collection on the whole assembly. Per instance costs one constructor
/// argument and keeps the suite parallel.
/// </para>
/// </summary>
public sealed class CraftingRegistryTests
{
    /// <summary>A property kind inside the Scope B band that v1 registers nothing for.</summary>
    const ushort UnregisteredKind = 200;

    /// <summary>The currency whose one step is the rolling operation.</summary>
    const int RollCurrency = 20;

    /// <summary>The currency whose one step is an operation writing an unregistered kind.</summary>
    const int GameCurrency = 21;

    /// <summary>The currency whose one step draws a mod through the executor's own generator.</summary>
    const int DrawCurrency = 22;

    [Fact]
    public void Registration_runs_at_process_start_and_a_later_Register_THROWS()
    {
        var registry = new CraftingRegistry();
        registry.Register(new NoOpOperation(FirstGameOperation));

        Assert.True(registry.TryGet(FirstGameOperation, out ICraftOperation? held));
        Assert.Equal(FirstGameOperation, held.Id);

        // The same id twice is a programming error in a registration rather than data, so it throws at
        // startup rather than replacing what is there.
        _ = Assert.Throws<ArgumentException>(() => registry.Register(new NoOpOperation(FirstGameOperation)));

        // And once the first pack has loaded, every registration throws, because a currency row naming an
        // operation is already resolved against whatever was registered by then.
        registry.Freeze();
        _ = Assert.Throws<InvalidOperationException>(
            () => registry.Register(new NoOpOperation(FirstGameOperation + 1)));
    }

    [Fact]
    public void The_registry_freezes_at_the_FIRST_pack_load()
    {
        var registry = new CraftingRegistry();
        registry.Register(new NoOpOperation(FirstGameOperation));
        Assert.False(registry.IsFrozen);

        // The pack load IS the currency resolution, so the index a host registers against
        // crafting_currency is what freezes the registry it was handed.
        _ = Index(PolishingWorld(), registry);

        Assert.True(registry.IsFrozen);
        _ = Assert.Throws<InvalidOperationException>(
            () => registry.Register(new NoOpOperation(FirstGameOperation + 1)));

        // Freezing stays idempotent, so a second boot path calling it is not an error and lookup keeps
        // answering afterwards.
        registry.Freeze();
        Assert.True(registry.TryGet(FirstGameOperation, out _));
    }

    [Fact]
    public void An_operation_whose_Id_is_below_1024_is_refused_by_the_REGISTRY_itself()
    {
        var registry = new CraftingRegistry();

        // 1 to 14 is a primitive the engine owns, and 15 to 1,023 is the gap the step codec already
        // refuses. The band is the registry's own check rather than a rule in prose.
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.Register(new NoOpOperation((int)CraftPrimitive.Repair)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.Register(new NoOpOperation(FirstGameOperation - 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => registry.Register(new NoOpOperation(0)));

        registry.Register(new NoOpOperation(FirstGameOperation));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void An_operation_that_ROLLS_took_its_IRandomSource_in_ITS_OWN_constructor()
    {
        // The registry holds INSTANCES rather than types, so a game constructs its operation with the
        // source the process is running on. A type with no source in its constructor provably cannot roll.
        ConstructorInfo constructor = Assert.Single(typeof(RollingOperation).GetConstructors());
        Assert.Contains(
            constructor.GetParameters(),
            static parameter => parameter.ParameterType == typeof(IRandomSource));

        var recording = new RecordingRandomSource(new ScriptedRandomSource([], [1234]));
        var operations = new CraftingRegistry();
        operations.Register(new RollingOperation(FirstGameOperation, recording));

        CraftWorld world = World(RollingRows);
        CraftWorkingCopy copy = world.Open(Target());
        CraftOutcome outcome = Executor(world, new ScriptedRandomSource([]), Frozen(operations))
            .Apply(Plan(world, RollCurrency), ref copy);

        // The roll reached the source the OPERATION was constructed with, and nothing else drew at all.
        Assert.False(outcome.IsRefused);
        Assert.Equal(["position"], recording.Calls);
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal((ushort)1234, Assert.Single(GenerationWorld.Affixes(crafted)).Position);
    }

    [Fact]
    public void Apply_has_NO_random_parameter_which_is_what_makes_the_previous_fact_checkable()
    {
        MethodInfo? apply = typeof(ICraftOperation).GetMethod(nameof(ICraftOperation.Apply));
        Assert.NotNull(apply);

        Assert.DoesNotContain(
            apply.GetParameters(),
            static parameter => parameter.ParameterType == typeof(IRandomSource));

        // The whole interface is an id and one method, so there is nowhere else for a source to arrive.
        Assert.Equal(
            ["Apply", "Id", "get_Id"],
            typeof(ICraftOperation).GetMembers()
                .Select(static member => member.Name)
                .OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void The_registry_is_PER_INSTANCE_and_never_a_static_so_no_test_needs_a_collection()
    {
        // Nothing static holds a registration, so two registries built by two parallel facts cannot see
        // each other and no fact here writes process-global state.
        Assert.DoesNotContain(
            typeof(CraftingRegistry).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            static field => !field.IsLiteral);
        Assert.Empty(typeof(CraftingRegistry)
            .GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

        var first = new CraftingRegistry();
        var second = new CraftingRegistry();
        first.Register(new NoOpOperation(FirstGameOperation));

        Assert.True(first.TryGet(FirstGameOperation, out _));
        Assert.False(second.TryGet(FirstGameOperation, out _));

        first.Freeze();
        Assert.False(second.IsFrozen);
    }

    [Fact]
    public void A_game_operation_that_writes_an_unregistered_kind_THROWS_at_encode()
    {
        // Spec 10.5's first power, and the SHIPPED door is the working copy's one write rather than the
        // encode: the write is refused BY KIND, so the operation never builds a field the encoder would
        // have to reject, and the encode then answers false because the craft refused. The item cannot
        // carry the kind either way, which is the property this name is about.
        var operations = new CraftingRegistry();
        operations.Register(new UnregisteredKindOperation(FirstGameOperation));

        CraftWorld world = World(GameRows);
        byte[] stored = Target();
        CraftWorkingCopy copy = world.Open(stored);
        CraftOutcome outcome = Executor(world, new ScriptedRandomSource([]), Frozen(operations))
            .Apply(Plan(world, GameCurrency), ref copy);

        Assert.Equal(new CraftRefusal(CraftRefusalKind.PropertyKindUnregistered, UnregisteredKind), outcome.Refusal);
        Assert.False(copy.TryEncode(out byte[] crafted));
        Assert.Empty(crafted);
    }

    [Fact]
    public void A_game_operation_cannot_allocate_an_instance_id()
    {
        // Spec 10.5's fourth power, and it is a property of the TYPE rather than a check: there is no
        // allocator anywhere in the working copy and none arrives through Apply, so an operation has
        // nothing to ask.
        Assert.DoesNotContain(
            typeof(CraftWorkingCopy).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static field => field.FieldType == typeof(InstanceIdAllocator));
        Assert.DoesNotContain(
            typeof(CraftWorkingCopy).GetProperties(),
            static property => property.PropertyType == typeof(InstanceIdAllocator));

        MethodInfo? apply = typeof(ICraftOperation).GetMethod(nameof(ICraftOperation.Apply));
        Assert.NotNull(apply);
        Assert.DoesNotContain(
            apply.GetParameters(),
            static parameter => parameter.ParameterType == typeof(InstanceIdAllocator));

        // And the executor's own generator is built over a store that refuses every request, so a craft
        // that reached for an id would throw rather than mint one. A craft that DRAWS still runs, which is
        // what makes the refusing store a standing guard rather than a broken executor.
        CraftWorld world = World(DrawRows);
        CraftWorkingCopy copy = world.Open(Target(rarityId: RareRarity));
        Assert.False(Executor(world, new SeededRandomSource(915)).Apply(Plan(world, DrawCurrency), ref copy).IsRefused);
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal(2, GenerationWorld.Affixes(crafted).Count);
    }

    /// <summary>One currency whose single step is the game operation at the bottom of the band.</summary>
    static IEnumerable<ContentRow> RollingRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, RollCurrency, "roll_bench", maxSteps: 1),
            Step(registry, 201, "roll_bench_1", RollCurrency, sort: 1, operation: FirstGameOperation),
        ];

    /// <summary>The same shape for the operation that writes a kind nobody registered.</summary>
    static IEnumerable<ContentRow> GameRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, GameCurrency, "rogue_bench", maxSteps: 1),
            Step(registry, 211, "rogue_bench_1", GameCurrency, sort: 1, operation: FirstGameOperation),
        ];

    /// <summary>One currency that adds a random mod, which is the executor's own generator drawing.</summary>
    static IEnumerable<ContentRow> DrawRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, DrawCurrency, "draw_bench", maxSteps: 1),
            Step(
                registry,
                221,
                "draw_bench_1",
                DrawCurrency,
                sort: 1,
                operation: (int)CraftPrimitive.AddRandomMod,
                a: ModContentType.SuffixKind),
        ];

    /// <summary>An operation that does nothing, for the facts that are about the registration alone.</summary>
    sealed class NoOpOperation(int id) : ICraftOperation
    {
        /// <inheritdoc />
        public int Id => id;

        /// <inheritdoc />
        public CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters) => null;
    }

    /// <summary>
    /// An operation that ROLLS, taking its source in its OWN constructor because that is the only place
    /// one can arrive. It rewrites the first affix's roll position, which is the same draw
    /// <c>RerollValues</c> makes and is what a game's exotic bench would reach for.
    /// </summary>
    sealed class RollingOperation(int id, IRandomSource random) : ICraftOperation
    {
        /// <inheritdoc />
        public int Id => id;

        /// <inheritdoc />
        public CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters)
        {
            Span<InstanceAffix> entries = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
            int count = copy.ReadAffixes(InstancePropertyKind.Affixes, entries);
            if (count == 0)
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.AffixAbsent, 0));
            }

            entries[0] = entries[0] with { Position = random.NextRollPosition() };
            return copy.SetAffixes(InstancePropertyKind.Affixes, entries[..count]) ? null : copy.Refusal;
        }
    }

    /// <summary>An operation reaching for a property kind this process registered nothing for.</summary>
    sealed class UnregisteredKindOperation(int id) : ICraftOperation
    {
        /// <inheritdoc />
        public int Id => id;

        /// <inheritdoc />
        public CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters)
            => copy.SetScalar(UnregisteredKind, 1) ? null : copy.Refusal;
    }
}
