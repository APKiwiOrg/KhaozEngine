using System;
using KhaozEngine.Catalog;
using KhaozEngine.Diagnostics;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Everything <see cref="ContainerLoad.Load"/> needs besides the stored sections and the active snapshot,
/// which contracts 14.4 says arrives by argument: no ambient statics and no service locator.
/// <para>
/// <b>It is a record of arguments rather than a service.</b> Spec 5.5 writes the load as
/// <c>Load(sections, snapshot)</c>, and the pass and the validator need four more things by name (the two
/// registries, the rule set and the door predicate) plus the two sinks the telemetry call takes. Passing
/// eight arguments positionally is how a caller puts the registries the wrong way round, so they arrive as
/// one named thing that is built once per container.
/// </para>
/// <para>
/// <b>The rule set is vetted HERE, once.</b> <c>RemapRuleSet.IsIdempotent</c> is a nested loop over the whole
/// rule list and the set does not change between a container's pages, so the quadratic walk happens on the
/// way in rather than once per page
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/928">#928</see>). A set that breaks contracts
/// 8.3 is refused here, before a single page is decoded.
/// </para>
/// </summary>
public sealed class ContainerLoadContext
{
    /// <summary>
    /// Builds the context for one container on one stream.
    /// </summary>
    /// <param name="streamKey">The stream the sections are filed under, which is the only identifying thing
    /// the log line carries (contracts 10.2 forbids the payload bytes and a raw account id). A section on any
    /// other stream is not this container's.</param>
    /// <param name="container">The container name, the first half of every one of its section names.</param>
    /// <param name="properties">The property kinds this build knows.</param>
    /// <param name="types">The content types the active pack registers.</param>
    /// <param name="rules">The full ordered rule set the active pack carries.</param>
    /// <param name="stackable">The game's rule for whether a definition merges into one slot, handed to each
    /// page's inner container. The load path never consults it.</param>
    /// <param name="logger">The logger, obtained under
    /// <see cref="InstanceValidationTelemetry.LogCategory"/> through <c>LogManager.GetLogger</c> rather than
    /// through the ambient facade. Null emits no line.</param>
    /// <param name="counter">Contracts 10.2's counter, called once per record out of play and once per
    /// abandoned remap. Null counts nothing.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="container"/> is not a name
    /// <see cref="ContainerSectionNames.Format"/> would accept, or the rule set is not idempotent.</exception>
    public ContainerLoadContext(
        string streamKey,
        string container,
        InstancePropertyRegistry properties,
        ContentTypeRegistry types,
        RemapRuleSet rules,
        Func<int, bool> stackable,
        ILogger? logger = null,
        Action<int, string>? counter = null)
    {
        ArgumentNullException.ThrowIfNull(streamKey);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stackable);

        // The container name is checked by FORMATTING one, so a context cannot be built for a container whose
        // sections this scheme could not have written.
        _ = ContainerSectionNames.Format(container, 0);

        StreamKey = streamKey;
        Container = container;
        Properties = properties;
        Types = types;
        Rules = VettedRemapRules.Vet(rules);
        Stackable = stackable;
        Logger = logger;
        Counter = counter;
    }

    /// <summary>The stream the sections are filed under.</summary>
    public string StreamKey { get; }

    /// <summary>The container being loaded.</summary>
    public string Container { get; }

    /// <summary>The property kinds this build knows.</summary>
    public InstancePropertyRegistry Properties { get; }

    /// <summary>The content types the active pack registers.</summary>
    public ContentTypeRegistry Types { get; }

    /// <summary>The rule set, walked for idempotence once for the whole container.</summary>
    public VettedRemapRules Rules { get; }

    /// <summary>The game's stackable predicate, handed to each page's inner container.</summary>
    public Func<int, bool> Stackable { get; }

    /// <summary>Where the one log line goes, or null.</summary>
    public ILogger? Logger { get; }

    /// <summary>Where the per record count goes, or null.</summary>
    public Action<int, string>? Counter { get; }
}
