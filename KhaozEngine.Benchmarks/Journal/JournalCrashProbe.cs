using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;

namespace KhaozEngine.Benchmarks.Journal;

public static class JournalCrashProbe
{
    private const string StreamKey = "player/crash-probe";
    private static readonly Guid InitializationOperationId = Guid.Parse("fc342193-5d04-4947-a438-98624e48be9d");

    public static JournalOperationIdentity CreateIdentity(Guid operationId)
        => new(operationId, "benchmark/crash", "inventory.grant", new byte[] { 71, 82, 65, 78, 84 });

    public static JournalCommit CreateCommit(Guid operationId)
        => new(
            CreateIdentity(operationId),
            new[]
            {
                new JournalStreamMutation(
                    StreamKey,
                    0,
                    new[] { new JournalEvent("inventory.item.granted", 1, new byte[] { 42, 1, 0, 0 }) }),
            },
            new[] { new JournalProjectionWrite(StreamKey, "bag", "bag.v1", 1, new byte[] { 42, 1, 0, 0 }) },
            "mutation.result.v1",
            1,
            new byte[] { 79, 75 });

    public static async Task RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        Parse(args, out string database, out Guid operationId, out string phaseName, out JournalTestHookPhase phase);
        int armed = 0;
        int paused = 0;
        var hook = new SqliteJournalTestHook(value =>
        {
            if (Volatile.Read(ref armed) == 0 || value != phase || Interlocked.Exchange(ref paused, 1) != 0) return;
            Console.Out.WriteLine($"JOURNAL_CHECKPOINT {phaseName}");
            Console.Out.Flush();
            _ = Console.In.ReadLine();
        });
        using var store = new SqliteMutationJournalStore(
            new SqliteMutationJournalStoreOptions($"Data Source={database};Pooling=False"),
            hook);

        JournalInitializeResult initialized = await store.InitializeAsync(
            new JournalInitialization(
                new JournalOperationIdentity(
                    InitializationOperationId,
                    "benchmark/crash",
                    "stream.initialize",
                    Encoding.ASCII.GetBytes(StreamKey)),
                StreamKey,
                "player.snapshot.v1",
                1,
                new byte[] { 1 },
                new[] { new JournalProjectionWrite(StreamKey, "bag", "bag.v1", 1, new byte[] { 1 }) },
                "initialize.result.v1",
                1,
                new byte[] { 1 }),
            cancellationToken).ConfigureAwait(false);
        if (initialized.Status is not (JournalInitializeStatus.Initialized or JournalInitializeStatus.Replayed))
            throw new InvalidOperationException($"Crash probe stream initialization failed with {initialized.Status}.");

        Volatile.Write(ref armed, 1);
        JournalCommitResult result = await store.CommitAsync(CreateCommit(operationId), cancellationToken).ConfigureAwait(false);
        if (result.Status is not (JournalCommitStatus.Applied or JournalCommitStatus.Replayed))
            throw new InvalidOperationException($"Crash probe operation failed with {result.Status}.");
    }

    private static void Parse(
        IReadOnlyList<string> args,
        out string database,
        out Guid operationId,
        out string phaseName,
        out JournalTestHookPhase phase)
    {
        string? databaseValue = null;
        Guid? operationValue = null;
        string? phaseValue = null;
        bool mode = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--journal-crash-probe":
                    if (mode) throw new ArgumentException("Crash probe mode may be specified once.", nameof(args));
                    mode = true;
                    break;
                case "--database":
                    databaseValue = Value(args, ref i, "--database");
                    break;
                case "--operation-id":
                    string operationText = Value(args, ref i, "--operation-id");
                    operationValue = Guid.TryParseExact(operationText, "D", out Guid parsed)
                        ? parsed
                        : throw new ArgumentException("Operation ID must be a D-format GUID.", nameof(args));
                    break;
                case "--pause-at":
                    phaseValue = Value(args, ref i, "--pause-at");
                    break;
                default:
                    throw new ArgumentException($"Unknown crash probe option '{args[i]}'.", nameof(args));
            }
        }

        if (!mode) throw new ArgumentException("Specify --journal-crash-probe.", nameof(args));
        database = databaseValue ?? throw new ArgumentException("Crash probe requires --database.", nameof(args));
        if (!Path.IsPathFullyQualified(database)) throw new ArgumentException("Crash probe database path must be absolute.", nameof(args));
        operationId = operationValue ?? throw new ArgumentException("Crash probe requires --operation-id.", nameof(args));
        if (operationId == Guid.Empty) throw new ArgumentException("Crash probe operation ID cannot be empty.", nameof(args));
        phaseName = phaseValue ?? throw new ArgumentException("Crash probe requires --pause-at.", nameof(args));
        phase = phaseName switch
        {
            "before-commit" => JournalTestHookPhase.BeforeCommit,
            "after-commit-before-response" => JournalTestHookPhase.AfterCommitBeforeResponse,
            _ => throw new ArgumentException("Pause phase must be before-commit or after-commit-before-response.", nameof(args)),
        };
    }

    private static string Value(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count) throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));
        return args[index];
    }
}
