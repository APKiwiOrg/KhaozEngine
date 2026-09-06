using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public enum SqlServerJournalSchemaMode
{
    AutoCreate,
    ValidateOnly,
}

public sealed record SqlServerMutationJournalStoreOptions(string ConnectionString)
{
    public SqlServerJournalSchemaMode SchemaMode { get; init; } = SqlServerJournalSchemaMode.AutoCreate;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinimumRetryHorizon { get; init; } = TimeSpan.FromHours(24);
    public JournalLimits Limits { get; init; } = JournalLimits.Maximum;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed partial class SqlServerMutationJournalStore : IMutationJournalStore, IMutationJournalMaintenance
{
    private readonly string connectionString;
    private readonly int commandTimeoutSeconds;
    private readonly JournalLimits limits;
    private readonly TimeSpan minimumRetryHorizon;
    private readonly TimeProvider timeProvider;
    private readonly SqlServerJournalTestHook? testHook;

    public SqlServerMutationJournalStore(string connectionString)
        : this(new SqlServerMutationJournalStoreOptions(connectionString), null)
    {
    }

    public SqlServerMutationJournalStore(SqlServerMutationJournalStoreOptions options)
        : this(options, null)
    {
    }

    internal SqlServerMutationJournalStore(
        SqlServerMutationJournalStoreOptions options,
        SqlServerJournalTestHook? testHook)
    {
        ArgumentNullException.ThrowIfNull(options);
        connectionString = options.ConnectionString
            ?? throw new ArgumentException("Connection string is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(options));
        if (!Enum.IsDefined(options.SchemaMode))
            throw new ArgumentOutOfRangeException(nameof(options), options.SchemaMode, "Schema mode is not supported.");
        if (options.CommandTimeout <= TimeSpan.Zero || options.CommandTimeout.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), options.CommandTimeout, "Command timeout must be positive and fit SQL Server's second timeout.");
        if (options.MinimumRetryHorizon < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.MinimumRetryHorizon, "Minimum retry horizon cannot be negative.");

        commandTimeoutSeconds = checked((int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        limits = options.Limits ?? throw new ArgumentNullException(nameof(options), "Limits cannot be null.");
        timeProvider = options.TimeProvider ?? throw new ArgumentNullException(nameof(options), "Time provider cannot be null.");
        minimumRetryHorizon = options.MinimumRetryHorizon;
        this.testHook = testHook;

        SqlServerJournalSchema.InitializeAsync(
            connectionString,
            options.SchemaMode,
            commandTimeoutSeconds,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<JournalOperationResolution> ResolveOperationAsync(
        JournalOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(identity);
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
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
        catch (SqlException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw MapProviderFailure(exception.Number, exception, Array.Empty<string>(), false, false, rolledBack);
        }
        catch (OperationCanceledException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw Cancelled(Array.Empty<string>(), false, false, rolledBack, exception);
        }
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw Cancelled(Array.Empty<string>(), false, false, true, exception);
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw MapProviderFailure(exception.Number, exception, Array.Empty<string>(), false, false, false);
        }
    }

    private static async Task<SqlTransaction> BeginTransactionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(Array.Empty<string>(), false, false, true, exception);
        }
        catch (SqlException exception)
        {
            throw MapProviderFailure(exception.Number, exception, Array.Empty<string>(), false, false, false);
        }
    }

    private async Task AcquireMaintenanceLockAsync(
        SqlTransaction transaction,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        using SqlCommand command = CreateCommand(transaction, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'KhaozEngine.WorldStore.SqlServer.JournalMaintenance',
                @LockMode = @mode,
                @LockOwner = 'Transaction',
                @LockTimeout = @timeout;
            SELECT @result;
            """);
        Add(command, "@mode", exclusive ? "Exclusive" : "Shared");
        Add(command, "@timeout", (int)Math.Min((long)commandTimeoutSeconds * 1000L, int.MaxValue));
        int result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result < 0) throw SqlServerJournalSchema.ApplicationLockFailure(result);
    }

    private SqlCommand CreateCommand(SqlTransaction transaction, string sql)
    {
        SqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("SQL transaction has no connection.");
        SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }

    private SqlCommand CreateCommand(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }

    private static void Add(SqlCommand command, string name, object value)
    {
        if (value is string textValue)
        {
            int maximum = name.Contains("stream", StringComparison.OrdinalIgnoreCase) ? 256 : 128;
            if (textValue.Length > maximum)
                throw new ArgumentOutOfRangeException(name, textValue.Length, $"SQL text parameter cannot exceed {maximum} characters.");
        }
        if (value is byte[] byteValue && byteValue.Length > JournalLimits.EngineMaximumAggregateCommitBytes)
            throw new ArgumentOutOfRangeException(name, byteValue.Length, "SQL binary parameter exceeds the journal binding limit.");
        SqlParameter parameter = value switch
        {
            string text => command.Parameters.Add(name, SqlDbType.NVarChar, name.Contains("stream", StringComparison.OrdinalIgnoreCase) ? 256 : 128),
            byte[] bytes when bytes.Length == 32 && IsFixedDigest(name) => command.Parameters.Add(name, SqlDbType.Binary, 32),
            byte[] => command.Parameters.Add(name, SqlDbType.VarBinary, -1),
            Guid => command.Parameters.Add(name, SqlDbType.UniqueIdentifier),
            DateTimeOffset => command.Parameters.Add(name, SqlDbType.DateTimeOffset),
            ushort => command.Parameters.Add(name, SqlDbType.Int),
            int => command.Parameters.Add(name, SqlDbType.Int),
            long => command.Parameters.Add(name, SqlDbType.BigInt),
            _ => throw new ArgumentException($"Unsupported SQL parameter type '{value.GetType().Name}'.", nameof(value)),
        };
        parameter.Value = value is ushort unsigned ? (int)unsigned : value;
    }

    private static bool IsFixedDigest(string name)
        => name.Contains("checksum", StringComparison.OrdinalIgnoreCase)
            || StringComparer.OrdinalIgnoreCase.Equals(name, "@intent")
            || StringComparer.OrdinalIgnoreCase.Equals(name, "@execution");

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();
    private static DateTimeOffset Timestamp(DateTimeOffset value) => value;
    private void Invoke(JournalTestHookPhase phase) => testHook?.Invoke(phase);

    private static async Task<bool> TryRollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static bool FingerprintsMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.Length == 32 && right.Length == 32 && CryptographicOperations.FixedTimeEquals(left, right);

    private static JournalStoreException MapProviderFailure(
        int number,
        Exception exception,
        IReadOnlyList<string> streamKeys,
        bool transactionStarted,
        bool commitStarted,
        bool rollbackConfirmed)
    {
        bool deadlockRolledBack = number == 1205;
        bool unknown = transactionStarted && !rollbackConfirmed && !deadlockRolledBack;
        JournalStoreFailureKind kind = unknown
            ? JournalStoreFailureKind.UnknownOutcome
            : number switch
            {
                1205 => JournalStoreFailureKind.Deadlock,
                1222 or -2 => JournalStoreFailureKind.Timeout,
                2601 or 2627 or 547 or 515 or 8115 => JournalStoreFailureKind.ConstraintViolation,
                _ => JournalStoreFailureKind.Unavailable,
            };
        JournalStoreFailureCertainty certainty = unknown
            ? JournalStoreFailureCertainty.Unknown
            : JournalStoreFailureCertainty.DefinitelyNotCommitted;
        return new JournalStoreException(
            kind,
            certainty,
            Scope(streamKeys),
            streamKeys,
            transactionStarted
                ? "SQL Server mutation journal transaction failed."
                : "SQL Server mutation journal connection or transaction could not be opened.",
            exception);
    }

    private static JournalStoreException Cancelled(
        IReadOnlyList<string> streamKeys,
        bool transactionStarted,
        bool commitStarted,
        bool rollbackConfirmed,
        Exception exception)
    {
        bool unknown = transactionStarted && !rollbackConfirmed;
        return new JournalStoreException(
            unknown ? JournalStoreFailureKind.UnknownOutcome : JournalStoreFailureKind.Cancelled,
            unknown ? JournalStoreFailureCertainty.Unknown : JournalStoreFailureCertainty.DefinitelyNotCommitted,
            Scope(streamKeys),
            streamKeys,
            "SQL Server mutation journal operation was cancelled.",
            exception);
    }

    private static JournalStoreException UnknownOutcome(IReadOnlyList<string> streamKeys, Exception exception)
        => new(
            JournalStoreFailureKind.UnknownOutcome,
            JournalStoreFailureCertainty.Unknown,
            Scope(streamKeys),
            streamKeys,
            "SQL Server mutation journal commit completed or may have completed before the response failed.",
            exception);

    private static JournalStoreException Corrupt(IReadOnlyList<string> streamKeys, string message)
        => new(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            Scope(streamKeys),
            streamKeys,
            message);

    private static JournalStoreFailureScope Scope(IReadOnlyList<string> streamKeys)
        => streamKeys.Count == 0 ? JournalStoreFailureScope.WholeStore : JournalStoreFailureScope.OperationStreams;

    internal static JournalStoreException MapFailureForTest(
        int number,
        bool transactionStarted,
        bool commitStarted,
        bool rollbackConfirmed,
        IReadOnlyList<string> streamKeys)
        => MapProviderFailure(number, new InvalidOperationException("provider test seam"), streamKeys, transactionStarted, commitStarted, rollbackConfirmed);

    internal static JournalStoreException MapCancellationForTest(
        bool transactionStarted,
        bool commitStarted,
        bool rollbackConfirmed,
        IReadOnlyList<string> streamKeys)
        => Cancelled(streamKeys, transactionStarted, commitStarted, rollbackConfirmed, new OperationCanceledException());

    internal static JournalStoreException MapTransportFailureForTest(
        Exception exception,
        bool commitStarted,
        bool rollbackConfirmed,
        IReadOnlyList<string> streamKeys)
        => commitStarted && !rollbackConfirmed
            ? UnknownOutcome(streamKeys, exception)
            : new JournalStoreException(
                JournalStoreFailureKind.Unavailable,
                JournalStoreFailureCertainty.DefinitelyNotCommitted,
                Scope(streamKeys),
                streamKeys,
                "SQL Server mutation journal transport failed.",
                exception);

    internal static string[] OrderStreamKeysForTest(IEnumerable<string> streamKeys)
        => streamKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal static JournalStoreException MapApplicationLockFailureForTest(int returnCode)
        => SqlServerJournalSchema.ApplicationLockFailure(returnCode);

    internal static string SchemaSqlForTest => SqlServerJournalSchema.SchemaSql;

    private enum OperationLookupStatus
    {
        NotFound,
        Conflict,
        Replayed,
    }

    private sealed record OperationLookup(OperationLookupStatus Status, JournalCommitReceipt? Receipt = null);
}
