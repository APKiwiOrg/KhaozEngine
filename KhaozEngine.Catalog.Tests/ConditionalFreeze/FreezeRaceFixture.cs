using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// One catalog for one forced race, on either backend, and everything the race needs to end cleanly: the
/// registry, the file and pack lifetime, a decorator per participant over that participant's own store, the
/// upgrade definitions, and the gates and participant tasks the test started.
/// <para>
/// <b>In memory, every participant wraps the one shared store</b>, which is the only way that store can be
/// shared. <b>On SQLite every participant gets its own initialized store instance on the one file</b> and the
/// same pack root, which reproduces separate replicas with separate connections. Each participant's id
/// persistence is its own store.
/// </para>
/// <para>
/// <b>Disposal is the bounded cleanup.</b> Every gate is disposed, which resumes anything still parked, and
/// every participant is awaited under <see cref="Watchdog"/>. A participant that does not end in time fails
/// the test loudly. Time never advances the scenario, it only bounds a hang.
/// </para>
/// </summary>
internal sealed class FreezeRaceFixture : IAsyncDisposable
{
    /// <summary>How long a participant may take to reach a gate or to finish before the test fails.</summary>
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    /// <summary>The identity a console operator edits and publishes under, which no upgrade runner carries.</summary>
    public const string ConsoleActor = "freeze-race-console";

    readonly bool _sqlite;
    readonly TemporaryCatalogDatabase _files = new();
    readonly FileSystemPackStore _packs;
    readonly List<SqliteContentAuthoringStore> _replicas = [];
    readonly List<OnceGate> _gates = [];
    readonly List<Task> _participants = [];

    /// <summary>Builds the catalog's registry, store and upgrade definitions. Nothing is written yet.</summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory reference store.</param>
    public FreezeRaceFixture(bool sqlite)
    {
        _sqlite = sqlite;
        Registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        _packs = _files.Pack();
        Inner = sqlite
            ? new SqliteContentAuthoringStore(_files.ConnectionString, Registry, _packs)
            : new InMemoryContentAuthoringStore(Registry, _packs);

        ContentBundle target = UpgradeFixtures.Target(
            Registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        First = UpgradeFixtures.Adds(
            UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row"));
        Second = UpgradeFixtures.Adds(
            UpgradeHarness.SecondId, 2, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row"));
    }

    /// <summary>The catalog's registry, carrying both fixture types.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The catalog itself, which the test reads and edits through as the console does.</summary>
    public IContentAuthoringStore Inner { get; }

    /// <summary>The shared pack root.</summary>
    public IPackStore Packs => _packs;

    /// <summary>The first upgrade, which adds <c>new_row</c> with its committed id.</summary>
    public ContentUpgradeDefinition First { get; }

    /// <summary>The second upgrade, which adds <c>second_new_row</c> with its committed id.</summary>
    public ContentUpgradeDefinition Second { get; }

    /// <summary>The id the seed gave <c>old_row</c>, read back after <see cref="SeedAsync"/>.</summary>
    public int OldRowId { get; private set; }

    /// <summary>Creates the catalog and publishes version 1 holding <c>old_row</c> at value 11.</summary>
    public async Task SeedAsync()
    {
        await Inner.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await Inner.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            ConsoleActor,
            "oid:freeze-race",
            "seed the catalog");
        await Inner.PublishAsync(PublishFixtures.Request(0));

        ContentRowPage rows = await Inner.ListRowsAsync(UpgradeFixtures.Thing, 0, "old_row", false, 0, 10);
        OldRowId = rows.Rows[0].Id;
    }

    /// <summary>The console's row-only update of <c>old_row</c>.</summary>
    /// <param name="value">The new value.</param>
    public ContentEdit OldRow(int value)
        => ContentEdit.Update(UpgradeFixtures.Thing, OldRowId, new ContentKey("old_row"), PublishFixtures.Fields(value));

    /// <summary>Applies edits into the open draft as a console operator.</summary>
    /// <param name="edits">The edits.</param>
    public Task<ContentDraft> EditAsync(params ContentEdit[] edits)
        => Inner.ApplyEditsAsync(edits, ConsoleActor, "oid:freeze-race", "console edit");

    /// <summary>A participant that declares the whole text companion and the ledger and publishes over itself.</summary>
    public async Task<FreezeRaceStore> TextParticipantAsync()
    {
        IContentAuthoringStore store = await ReplicaAsync();
        return new FreezeRaceStore(store, (IContentIdPersistence)store, Registry, new FreezeRacePackStore(_packs));
    }

    /// <summary>A row store plus ledger participant, publishing over itself or through the inner store.</summary>
    /// <param name="forwardPublish">Whether its publish is forwarded to the inner engine store.</param>
    public async Task<RowFreezeRaceStore> RowParticipantAsync(bool forwardPublish)
    {
        IContentAuthoringStore store = await ReplicaAsync();
        return new RowFreezeRaceStore(
            store, (IContentIdPersistence)store, Registry, new FreezeRacePackStore(_packs), forwardPublish);
    }

    /// <summary>A gate this fixture disposes at the end of the test.</summary>
    public OnceGate Gate()
    {
        var gate = new OnceGate();
        _gates.Add(gate);
        return gate;
    }

    /// <summary>
    /// Starts one participant on the thread pool, because the in-memory store answers synchronously and a bare
    /// call would run until its first pause on the test's own thread.
    /// </summary>
    /// <param name="participant">The participant's whole work.</param>
    public Task<T> Start<T>(Func<Task<T>> participant)
    {
        Task<T> task = Task.Run(participant);
        _participants.Add(task);
        return task;
    }

    /// <summary>
    /// Waits for a participant to park on its gate. A participant that ended first, or one that took longer
    /// than the watchdog, fails the test with what it did.
    /// </summary>
    /// <param name="gate">The gate the participant must reach.</param>
    /// <param name="participant">The participant.</param>
    /// <param name="what">The step, for the failure message.</param>
    public static async Task ReachAsync(OnceGate gate, Task participant, string what)
    {
        Task first = await Task.WhenAny(gate.Entered, participant).WaitAsync(Watchdog);
        if (first != gate.Entered)
        {
            throw new XunitException(
                $"the participant ended before it reached {what}: {Describe(Failure(participant))}");
        }
    }

    /// <summary>A participant's result or its failure, once it has ended within the watchdog.</summary>
    /// <param name="participant">The participant.</param>
    public static async Task<(T? Result, Exception? Failure)> OutcomeAsync<T>(Task<T> participant)
    {
        await Task.WhenAny(participant).WaitAsync(Watchdog);
        return participant.IsCompletedSuccessfully ? (participant.Result, null) : (default, Failure(participant));
    }

    /// <summary>One line naming a failure's refusal reason and message, or that there was none.</summary>
    /// <param name="failure">The failure, or null.</param>
    public static string Describe(Exception? failure) => failure switch
    {
        null => "completed",
        ContentAuthoringException refused => $"refused {refused.Reason}: {refused.Message}",
        _ => $"{failure.GetType().Name}: {failure.Message}",
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (OnceGate gate in _gates)
            {
                gate.Dispose();
            }

            await Task.WhenAny(Task.WhenAll(_participants)).WaitAsync(Watchdog);
        }
        finally
        {
            foreach (SqliteContentAuthoringStore replica in _replicas)
            {
                replica.Dispose();
            }

            (Inner as IDisposable)?.Dispose();
            _files.Dispose();
        }
    }

    /// <summary>The participant's own store: the shared one in memory, a fresh initialized replica on SQLite.</summary>
    async Task<IContentAuthoringStore> ReplicaAsync()
    {
        if (!_sqlite)
        {
            return Inner;
        }

        var replica = new SqliteContentAuthoringStore(_files.ConnectionString, Registry, _packs);
        _replicas.Add(replica);
        await replica.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        return replica;
    }

    static Exception? Failure(Task participant) => participant switch
    {
        { IsCanceled: true } => new TaskCanceledException(participant),
        { Exception: AggregateException faulted } => faulted.InnerExceptions.Count == 1
            ? faulted.InnerExceptions[0]
            : faulted,
        _ => null,
    };
}
