using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed record SqliteMutationJournalStoreOptions(string ConnectionString)
{
    public SqliteJournalSchemaMode SchemaMode { get; init; } = SqliteJournalSchemaMode.AutoCreate;
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MinimumRetryHorizon { get; init; } = TimeSpan.FromHours(24);
    public JournalLimits Limits { get; init; } = JournalLimits.Maximum;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed partial class SqliteMutationJournalStore : IMutationJournalStore, IMutationJournalAgeMaintenance, IDisposable
{
    private const string OperationDeleteGuardFunction = "khaoz_journal_operation_delete_allowed";
    internal static string VersionOneSchemaSqlForTest => SqliteJournalSchema.VersionOneSchemaSqlForTest;

    private readonly SqliteStoreConnection db;
    private readonly JournalLimits limits;
    private readonly TimeSpan minimumRetryHorizon;
    private readonly TimeProvider timeProvider;
    private readonly SqliteJournalTestHook? testHook;
    private bool operationDeleteGuardOpen;

    public SqliteMutationJournalStore(string connectionString)
        : this(new SqliteMutationJournalStoreOptions(connectionString), null)
    {
    }

    public SqliteMutationJournalStore(SqliteMutationJournalStoreOptions options)
        : this(options, null)
    {
    }

    internal SqliteMutationJournalStore(SqliteMutationJournalStoreOptions options, SqliteJournalTestHook? testHook)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionString);
        if (!Enum.IsDefined(options.SchemaMode)) throw new ArgumentOutOfRangeException(nameof(options), options.SchemaMode, "Schema mode is not supported.");
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), options.BusyTimeout, "Busy timeout must be positive and fit SQLite's millisecond timeout.");
        if (options.MinimumRetryHorizon < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.MinimumRetryHorizon, "Minimum retry horizon cannot be negative.");
        limits = options.Limits ?? throw new ArgumentNullException(nameof(options), "Limits cannot be null.");
        timeProvider = options.TimeProvider ?? throw new ArgumentNullException(nameof(options), "Time provider cannot be null.");
        minimumRetryHorizon = options.MinimumRetryHorizon;
        this.testHook = testHook;

        SqliteStoreConnection? connection = null;
        try
        {
            connection = new SqliteStoreConnection(options.ConnectionString, SqliteJournalSchema.BootstrapSql(options));
            connection.Connection.DefaultTimeout = checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds));
            connection.Connection.CreateFunction(
                OperationDeleteGuardFunction,
                () => operationDeleteGuardOpen ? 1 : 0,
                isDeterministic: false);
            SqliteJournalSchema.Initialize(connection.Connection, options.SchemaMode);
            db = connection;
        }
        catch (JournalStoreException)
        {
            connection?.Dispose();
            throw;
        }
        catch (SqliteException exception)
        {
            connection?.Dispose();
            throw MapProviderFailure(exception, Array.Empty<string>(), transactionStarted: false, commitStarted: false, rollbackConfirmed: false);
        }
    }

    private void OpenOperationDeleteGuard()
    {
        if (operationDeleteGuardOpen)
            throw Corrupt(Array.Empty<string>(), "SQLite operation delete guard was already open.");
        operationDeleteGuardOpen = true;
    }

    private void CloseOperationDeleteGuard() => operationDeleteGuardOpen = false;

    public async Task<JournalOperationResolution> ResolveOperationAsync(
        JournalOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(identity);
        Invoke(JournalTestHookPhase.BeforeTransaction);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = db.Connection.BeginTransaction(deferred: true);
        try
        {
            OperationLookup lookup = await LookupOperationAsync(
                identity.OperationId,
                intent.Digest.ToArray(),
                transaction,
                cancellationToken).ConfigureAwait(false);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lookup.Status switch
            {
                OperationLookupStatus.NotFound => new JournalOperationResolution(JournalOperationResolutionStatus.NotFound),
                OperationLookupStatus.Conflict => new JournalOperationResolution(JournalOperationResolutionStatus.OperationConflict),
                _ => new JournalOperationResolution(JournalOperationResolutionStatus.Replayed, lookup.Receipt),
            };
        }
        catch (SqliteException exception)
        {
            bool rolledBack = TryRollback(transaction);
            throw MapProviderFailure(exception, Array.Empty<string>(), transactionStarted: true, commitStarted: false, rolledBack);
        }
    }

    public void Dispose() => db.Dispose();

    private SqliteCommand CreateCommand(SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);

    private static string OperationId(Guid value) => value.ToString("D");
    private DateTimeOffset UtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    private static long Timestamp(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    private static DateTimeOffset Timestamp(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
    private void Invoke(JournalTestHookPhase phase) => testHook?.Invoke(phase);

    private static bool TryRollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static JournalStoreException MapProviderFailure(
        SqliteException exception,
        IReadOnlyList<string> streamKeys,
        bool transactionStarted,
        bool commitStarted,
        bool rollbackConfirmed)
    {
        bool unknown = commitStarted && !rollbackConfirmed;
        JournalStoreFailureKind kind = unknown
            ? JournalStoreFailureKind.UnknownOutcome
            : exception.SqliteErrorCode switch
            {
                5 or 6 => JournalStoreFailureKind.Timeout,
                19 => JournalStoreFailureKind.ConstraintViolation,
                _ => JournalStoreFailureKind.Unavailable,
            };
        JournalStoreFailureCertainty certainty = unknown
            ? JournalStoreFailureCertainty.Unknown
            : JournalStoreFailureCertainty.DefinitelyNotCommitted;
        JournalStoreFailureScope scope = streamKeys.Count == 0
            ? JournalStoreFailureScope.WholeStore
            : JournalStoreFailureScope.OperationStreams;
        return new JournalStoreException(
            kind,
            certainty,
            scope,
            streamKeys,
            transactionStarted
                ? "SQLite mutation journal transaction failed."
                : "SQLite mutation journal connection or transaction could not be opened.",
            exception);
    }

    private static JournalStoreException UnknownOutcome(IReadOnlyList<string> streamKeys, Exception exception)
        => new(
            JournalStoreFailureKind.UnknownOutcome,
            JournalStoreFailureCertainty.Unknown,
            streamKeys.Count == 0 ? JournalStoreFailureScope.WholeStore : JournalStoreFailureScope.OperationStreams,
            streamKeys,
            "SQLite mutation journal commit completed or may have completed before the response failed.",
            exception);

    private static JournalStoreException Corrupt(IReadOnlyList<string> streamKeys, string message)
        => new(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            streamKeys.Count == 0 ? JournalStoreFailureScope.WholeStore : JournalStoreFailureScope.OperationStreams,
            streamKeys,
            message);

    private static bool FingerprintsMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.Length == 32 && right.Length == 32 && CryptographicOperations.FixedTimeEquals(left, right);

    private enum OperationLookupStatus
    {
        NotFound,
        Conflict,
        Replayed,
    }

    private sealed record OperationLookup(OperationLookupStatus Status, JournalCommitReceipt? Receipt = null);
}
