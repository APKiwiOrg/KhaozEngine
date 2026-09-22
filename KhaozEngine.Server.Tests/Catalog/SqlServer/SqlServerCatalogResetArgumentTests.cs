using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The arguments the audit row's own CHECK constraints will refuse, refused BEFORE the transaction opens,
/// mirroring <c>SqliteCatalogResetArgumentTests</c>.
/// <para>
/// <b>This backend measures them the way <c>LEN</c> does.</b> T-SQL's <c>LEN</c> ignores trailing spaces, so
/// an actor of one letter and two hundred blanks satisfies <c>ck_catalog_audit_actor</c> and an actor of two
/// hundred blanks alone does not, and both of those cases are pinned below.
/// </para>
/// <para>
/// The gated facts prove nothing was dropped. <see cref="ABadArgumentIsRefusedBeforeAnyConnectionOpens"/>
/// needs no instance, runs everywhere, and proves the check comes before the open.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetArgumentTests
{
    const string Actor = SqlServerCatalogResetHarness.Actor;
    const string Operator = SqlServerCatalogResetHarness.Operator;

    [CatalogSqlServerFact]
    public async Task AnArgumentTheAuditColumnCannotHoldIsRefusedWithNothingDropped()
    {
        using var database = new SqlServerCatalogDatabase();
        string epochBefore = await SeedAsync(database);

        await Refused(database, string.Empty, Operator, "note");
        await Refused(database, new string(' ', 200), Operator, "note");
        await Refused(database, new string('a', 129), Operator, "note");
        await Refused(database, Actor, new string('o', 129), "note");
        await Refused(database, Actor, Operator, new string('n', 1025));

        await AssertUntouched(database, epochBefore);
    }

    /// <summary>
    /// The connection string names a port nothing listens on, so a reset that opened a connection before
    /// checking its arguments would fail with the provider's connect error instead.
    /// </summary>
    [Fact]
    public async Task ABadArgumentIsRefusedBeforeAnyConnectionOpens()
    {
        const string Unreachable = "Server=127.0.0.1,1;Database=absent;User Id=nobody;Password=none;Connect Timeout=1;Encrypt=False";

        await Assert.ThrowsAsync<SqlException>(
            () => SqlServerCatalogReset.ResetAsync(Unreachable, Actor, Operator, "note"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => SqlServerCatalogReset.ResetAsync(Unreachable, new string(' ', 200), Operator, "note"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SqlServerCatalogReset.ResetAsync(Unreachable, Actor, Operator, new string('n', 1025)));
    }

    [CatalogSqlServerFact]
    public async Task EveryArgumentAtItsColumnsLimitIsAccepted()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString,
            new string('a', 128),
            new string('o', 128),
            new string('n', 1024));

        Assert.Equal(1, reset.ActiveVersion);
        Assert.Equal(128, database.Scalar("SELECT LEN(actor) FROM dbo.catalog_audit;"));
        Assert.Equal(128, database.Scalar("SELECT LEN([operator]) FROM dbo.catalog_audit;"));
        Assert.Equal(1024, database.Scalar("SELECT LEN(note) FROM dbo.catalog_audit;"));

        // The summary is what goes into before_value, whose CHECK caps it at 4096. It is a fixed sentence
        // around two 64-character hashes, a 32-character epoch and five numbers, so it cannot approach that.
        Assert.True(
            database.Scalar("SELECT LEN(before_value) FROM dbo.catalog_audit;") < 4096,
            "The reset summary must stay well under the audit column's 4096 cap.");
    }

    [CatalogSqlServerFact]
    public async Task AnActorThatIsOneLetterAndTwoHundredBlanksIsWhatLenSaysItIs()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);

        // LEN counts 1, the CHECK is satisfied, and SQL Server drops the trailing blanks that do not fit the
        // column without complaining. Measuring the raw string length here would refuse a legal actor.
        await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, "a" + new string(' ', 200), Operator, "content release");

        Assert.Equal(1, database.Scalar("SELECT LEN(actor) FROM dbo.catalog_audit;"));
    }

    [CatalogSqlServerFact]
    public async Task ANullArgumentIsStillANullArgument()
    {
        using var database = new SqlServerCatalogDatabase();
        await SeedAsync(database);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqlServerCatalogReset.ResetAsync(database.ConnectionString, null!, "oid", "note"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", null!, "note"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", "oid", null!));
    }

    static async Task Refused(
        SqlServerCatalogDatabase database,
        string actor,
        string operatorId,
        string note)
        => await Assert.ThrowsAnyAsync<ArgumentException>(
            () => SqlServerCatalogReset.ResetAsync(database.ConnectionString, actor, operatorId, note));

    /// <summary>A published store with a version, rows and an epoch to compare against.</summary>
    static async Task<string> SeedAsync(SqlServerCatalogDatabase database)
    {
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
        return await store.GetStoreEpochAsync();
    }

    /// <summary>The store exactly as it stood, down to the epoch a recreate would have replaced.</summary>
    static async Task AssertUntouched(SqlServerCatalogDatabase database, string epochBefore)
    {
        Assert.Equal(epochBefore, SqlServerCatalogResetHarness.Text(
            database, "SELECT store_epoch FROM dbo.catalog_metadata;"));
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));

        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());
    }
}
